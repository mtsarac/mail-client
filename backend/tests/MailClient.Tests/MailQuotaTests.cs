using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MailClient.Application.Accounts;
using MailClient.Application.Mail;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Mail;
using MailClient.Infrastructure.Network;
using MailClient.Infrastructure.Persistence;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace MailClient.Tests;

public sealed class FakeMailQuotaService : IMailQuotaService
{
    public ConcurrentDictionary<Guid, MailQuota> Quotas { get; } = new();
    public ConcurrentBag<Guid> Calls { get; } = [];

    public Task<MailQuota?> GetAsync(MailAccount account, CancellationToken cancellationToken)
    {
        Calls.Add(account.Id);
        return Task.FromResult(Quotas.TryGetValue(account.Id, out var quota) ? quota : null);
    }
}

public sealed class MailQuotaApiFactory : MailClientApiFactory
{
    public FakeMailQuotaService Quota { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMailQuotaService>();
            services.AddSingleton<IMailQuotaService>(Quota);
        });
    }
}

public sealed class MailQuotaApiTests(MailQuotaApiFactory factory) : IClassFixture<MailQuotaApiFactory>
{
    [Fact]
    public async Task Quota_IsReadForTheTokenAccountOnly_AndUnsupportedServersReportUnavailable()
    {
        var withQuota = await SeedAsync();
        var withoutQuota = await SeedAsync();
        factory.Quota.Quotas[withQuota] = new MailQuota(10 * 1024, 512 * 1024);

        var supported = await Client(withQuota).GetFromJsonAsync<JsonElement>("/api/account/quota");
        Assert.True(supported.GetProperty("available").GetBoolean());
        Assert.Equal(10 * 1024, supported.GetProperty("usedBytes").GetInt64());
        Assert.Equal(512 * 1024, supported.GetProperty("limitBytes").GetInt64());

        var unsupported = await Client(withoutQuota).GetFromJsonAsync<JsonElement>("/api/account/quota");
        Assert.False(unsupported.GetProperty("available").GetBoolean());
        Assert.Equal(JsonValueKind.Null, unsupported.GetProperty("usedBytes").ValueKind);
        Assert.Equal(JsonValueKind.Null, unsupported.GetProperty("limitBytes").ValueKind);

        Assert.Equal(1, factory.Quota.Calls.Count(id => id == withQuota));
        Assert.Equal(1, factory.Quota.Calls.Count(id => id == withoutQuota));
    }

    [Fact]
    public async Task Quota_RequiresAuthentication()
    {
        var response = await factory.CreateClient().GetAsync("/api/account/quota");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private HttpClient Client(Guid accountId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            factory.Services.GetRequiredService<IJwtTokenIssuer>().Issue(accountId).Token);
        return client;
    }

    private async Task<Guid> SeedAsync()
    {
        var accountId = Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.MailAccounts.Add(new MailAccount
        {
            Id = accountId,
            EmailAddress = $"{accountId:N}@mail.test.invalid",
            NormalizedEmailAddress = $"{accountId:N}@MAIL.TEST.INVALID",
            Username = accountId.ToString("N"),
            ImapHost = "mail.test.invalid",
            ImapPort = 993,
            SmtpHost = "mail.test.invalid",
            SmtpPort = 465,
            Status = MailAccountStatus.Active
        });
        await db.SaveChangesAsync();
        return accountId;
    }
}

public sealed class MailQuotaServiceTests
{
    [Fact]
    public async Task ReadAsync_ConvertsRfcStorageKilobytesToBytes()
    {
        var quota = await ReadFromScriptedServerAsync(advertiseQuota: true,
            "* QUOTAROOT INBOX \"\"", "* QUOTA \"\" (STORAGE 10 512)");

        Assert.Equal(new MailQuota(10 * 1024, 512 * 1024), quota);
    }

    [Fact]
    public async Task ReadAsync_ServerWithoutQuotaCapability_IsUnavailable()
    {
        Assert.Null(await ReadFromScriptedServerAsync(advertiseQuota: false));
    }

    [Fact]
    public async Task ReadAsync_RejectedGetQuotaRoot_IsUnavailable()
    {
        Assert.Null(await ReadFromScriptedServerAsync(advertiseQuota: true, rejectQuota: true));
    }

    [Fact]
    public async Task ReadAsync_QuotaWithoutStorageResource_IsUnavailable()
    {
        Assert.Null(await ReadFromScriptedServerAsync(advertiseQuota: true,
            "* QUOTAROOT INBOX \"\"", "* QUOTA \"\" (MESSAGE 3 100)"));
    }

    [Fact]
    public async Task GetAsync_RejectedCredentials_IsUnavailableInsteadOfFailing()
    {
        await using var db = CreateDb();
        var account = await SeedAsync(db, MailAccountStatus.Active);
        var service = new MailQuotaService(TestServices.Credentials(db), new FailingConnectionHelper(),
            NullLogger<MailQuotaService>.Instance);

        Assert.Null(await service.GetAsync(account, CancellationToken.None));
    }

    [Fact]
    public async Task GetAsync_AccountAwaitingReauthentication_IsUnavailableWithoutConnecting()
    {
        await using var db = CreateDb();
        var account = await SeedAsync(db, MailAccountStatus.NeedsReauthentication);
        var connections = new FailingConnectionHelper();
        var service = new MailQuotaService(TestServices.Credentials(db), connections,
            NullLogger<MailQuotaService>.Instance);

        Assert.Null(await service.GetAsync(account, CancellationToken.None));
        Assert.Equal(0, connections.Calls);
    }

    private static async Task<MailQuota?> ReadFromScriptedServerAsync(bool advertiseQuota, params string[] quotaLines) =>
        await ReadFromScriptedServerAsync(advertiseQuota, rejectQuota: false, quotaLines);

    private static async Task<MailQuota?> ReadFromScriptedServerAsync(bool advertiseQuota, bool rejectQuota, params string[] quotaLines)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var capabilities = advertiseQuota ? "IMAP4rev1 QUOTA" : "IMAP4rev1";
        var server = RunScriptedImapServerAsync(listener, capabilities, rejectQuota, quotaLines);

        MailQuota? quota;
        using (var client = new ImapClient())
        {
            await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None);
            await client.AuthenticateAsync("user", "secret");
            quota = await MailQuotaService.ReadAsync(client, CancellationToken.None);
            await client.DisconnectAsync(true);
        }

        await server.WaitAsync(TimeSpan.FromSeconds(10));
        return quota;
    }

    /// <summary>Minimal plaintext IMAP peer: answers login/list generically and GETQUOTAROOT from the script.</summary>
    private static async Task RunScriptedImapServerAsync(TcpListener listener, string capabilities, bool rejectQuota, string[] quotaLines)
    {
        using var connection = await listener.AcceptTcpClientAsync();
        await using var stream = connection.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
        await writer.WriteLineAsync($"* OK [CAPABILITY {capabilities}] ready");
        while (await reader.ReadLineAsync() is { } line)
        {
            var parts = line.Split(' ', 3);
            var tag = parts[0];
            var command = parts.Length > 1 ? parts[1].ToUpperInvariant() : "";
            switch (command)
            {
                case "CAPABILITY":
                    await writer.WriteLineAsync($"* CAPABILITY {capabilities}");
                    await writer.WriteLineAsync($"{tag} OK done");
                    break;
                case "LOGIN":
                    await writer.WriteLineAsync($"{tag} OK [CAPABILITY {capabilities}] logged in");
                    break;
                case "LIST":
                    await writer.WriteLineAsync(line.Contains("INBOX", StringComparison.OrdinalIgnoreCase)
                        ? "* LIST (\\HasNoChildren) \"/\" INBOX"
                        : "* LIST (\\Noselect) \"/\" \"\"");
                    await writer.WriteLineAsync($"{tag} OK done");
                    break;
                case "GETQUOTAROOT":
                    if (rejectQuota)
                    {
                        await writer.WriteLineAsync($"{tag} NO quota not available");
                        break;
                    }
                    foreach (var quotaLine in quotaLines)
                        await writer.WriteLineAsync(quotaLine);
                    await writer.WriteLineAsync($"{tag} OK done");
                    break;
                case "LOGOUT":
                    await writer.WriteLineAsync("* BYE bye");
                    await writer.WriteLineAsync($"{tag} OK done");
                    return;
                default:
                    await writer.WriteLineAsync($"{tag} OK done");
                    break;
            }
        }
    }

    private static async Task<MailAccount> SeedAsync(AppDbContext db, MailAccountStatus status)
    {
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            EmailAddress = "person@example.test",
            NormalizedEmailAddress = "PERSON@EXAMPLE.TEST",
            Username = "person@example.test",
            ImapHost = "imap.example.test",
            ImapPort = 993,
            SmtpHost = "smtp.example.test",
            SmtpPort = 465,
            Status = status
        };
        db.MailAccounts.Add(account);
        db.MailCredentials.Add(new MailCredential
        {
            Id = Guid.NewGuid(),
            MailAccountId = account.Id,
            AuthenticationMethod = AuthenticationMethod.Password,
            Provider = MailProvider.Custom,
            EncryptedMaterial = "password",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return account;
    }

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private sealed class FailingConnectionHelper() : MailConnectionHelper(
        new OutboundHostValidator(new FakeDns(IPAddress.Loopback)),
        NullLogger<MailConnectionHelper>.Instance)
    {
        public int Calls { get; private set; }

        public override Task<T> WithImapAsync<T>(
            MailServerEndpoint endpoint,
            string username,
            string password,
            string operation,
            Func<ImapClient, CancellationToken, Task<T>> action,
            CancellationToken cancellationToken,
            AuthenticationMethod authenticationMethod = AuthenticationMethod.Password)
        {
            Calls++;
            throw new MailConnectionException(MailConnectionFailure.Authentication, "authentication failed") { Operation = operation };
        }
    }
}
