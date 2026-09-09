using MailClient.Application.Interfaces;
using MailClient.Application.Network;
using MailClient.Application.Validation;
using MailClient.Infrastructure.Network;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Security;
using MailClient.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace MailClient.Infrastructure.Tests.Services;

public class MailAccountServiceTests
{
    [Fact]
    public async Task CreateAsync_ProtectsPasswordAndDoesNotReturnIt()
    {
        var userId = Guid.NewGuid();
        await using var db = CreateDb();
        var protector = CreateProtector();
        var service = CreateService(db, protector);

        var result = await service.CreateAsync(userId, Request("mailbox-password"), CancellationToken.None);

        var stored = await db.MailAccounts.SingleAsync();
        Assert.NotEqual("mailbox-password", stored.EncryptedPassword);
        Assert.Equal("mailbox-password", protector.Unprotect(stored.EncryptedPassword));
        Assert.Equal("account@example.com", result.EmailAddress);
    }

    [Fact]
    public async Task CreateAsync_RejectsBlockedLiteralHost()
    {
        await using var db = CreateDb();
        var service = CreateService(db, CreateProtector(), hosts: new OutboundHostValidator(new NeverDnsResolver()));
        var request = Request("password") with { ImapHost = "127.0.0.1" };

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            service.CreateAsync(Guid.NewGuid(), request, CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_RejectsNullRequestFieldsWithValidationError()
    {
        await using var db = CreateDb();
        var service = CreateService(db, CreateProtector());
        var request = new MailAccountRequest(null!, null!, null!, "password", null!, 0, MailSecurity.SslOnConnect, null!, 0, MailSecurity.StartTls, true);

        var ex = await Assert.ThrowsAsync<RequestValidationException>(() =>
            service.CreateAsync(Guid.NewGuid(), request, CancellationToken.None));

        Assert.Contains("emailAddress", ex.Errors.Keys);
        Assert.Contains("imapHost", ex.Errors.Keys);
        Assert.Contains("imapPort", ex.Errors.Keys);
    }

    [Fact]
    public async Task GetAsync_DoesNotReturnAnotherUsersAccount()
    {
        await using var db = CreateDb();
        var service = CreateService(db, CreateProtector());
        var account = await service.CreateAsync(Guid.NewGuid(), Request("password"), CancellationToken.None);

        var result = await service.GetAsync(Guid.NewGuid(), account.Id, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateAsync_WithoutPassword_PreservesEncryptedCredential()
    {
        await using var db = CreateDb();
        var protector = CreateProtector();
        var service = CreateService(db, protector);
        var userId = Guid.NewGuid();
        var account = await service.CreateAsync(userId, Request("original-secret"), CancellationToken.None);
        var before = (await db.MailAccounts.SingleAsync()).EncryptedPassword;

        var updated = await service.UpdateAsync(userId, account.Id,
            new UpdateMailAccountRequest("account@example.com", "Renamed", "account@example.com", null,
                "imap.example.com", 993, MailSecurity.SslOnConnect,
                "smtp.example.com", 587, MailSecurity.StartTls, true),
            CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal("Renamed", updated.DisplayName);
        Assert.Equal(before, (await db.MailAccounts.SingleAsync()).EncryptedPassword);
        Assert.Equal("original-secret", protector.Unprotect((await db.MailAccounts.SingleAsync()).EncryptedPassword));
    }

    [Fact]
    public async Task UpdateAsync_WithPassword_ReplacesEncryptedCredential()
    {
        await using var db = CreateDb();
        var protector = CreateProtector();
        var service = CreateService(db, protector);
        var userId = Guid.NewGuid();
        var account = await service.CreateAsync(userId, Request("original-secret"), CancellationToken.None);
        var before = (await db.MailAccounts.SingleAsync()).EncryptedPassword;

        await service.UpdateAsync(userId, account.Id,
            new UpdateMailAccountRequest("account@example.com", "Account", "account@example.com", "rotated-secret",
                "imap.example.com", 993, MailSecurity.SslOnConnect,
                "smtp.example.com", 587, MailSecurity.StartTls, true),
            CancellationToken.None);

        var stored = await db.MailAccounts.SingleAsync();
        Assert.NotEqual(before, stored.EncryptedPassword);
        Assert.Equal("rotated-secret", protector.Unprotect(stored.EncryptedPassword));
        Assert.DoesNotContain("rotated-secret", stored.EncryptedPassword);
    }

    [Fact]
    public async Task UpdateAsync_WithBlankPassword_ThrowsValidationError()
    {
        await using var db = CreateDb();
        var service = CreateService(db, CreateProtector());
        var userId = Guid.NewGuid();
        var account = await service.CreateAsync(userId, Request("original-secret"), CancellationToken.None);

        await Assert.ThrowsAsync<RequestValidationException>(() => service.UpdateAsync(userId, account.Id,
            new UpdateMailAccountRequest("account@example.com", "Account", "account@example.com", "   ",
                "imap.example.com", 993, MailSecurity.SslOnConnect,
                "smtp.example.com", 587, MailSecurity.StartTls, true),
            CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_RejectsPlainSecurityOutsideDevelopment()
    {
        await using var db = CreateDb();
        var service = new MailAccountService(db, CreateProtector(), new TestEnvironment("Production"),
            new FakeConnectivityTester(), new AllowHostValidator(), NullLogger<MailAccountService>.Instance);
        var request = Request("password") with
        {
            ImapHost = "imap.example.com",
            SmtpHost = "smtp.example.com",
            ImapSecurity = MailSecurity.None
        };

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            service.CreateAsync(Guid.NewGuid(), request, CancellationToken.None));
    }

    [Fact]
    public async Task TestAsync_ReturnsFailureMessageFromTester()
    {
        await using var db = CreateDb();
        var tester = new FakeConnectivityTester
        {
            Failure = new MailConnectionException(MailConnectionFailure.Authentication, "Mail authentication failed.")
        };
        var service = CreateService(db, CreateProtector(), tester);
        var userId = Guid.NewGuid();
        var account = await service.CreateAsync(userId, Request("password"), CancellationToken.None);

        var result = await service.TestAsync(userId, account.Id, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.Succeeded);
        Assert.Equal("Mail authentication failed.", result.Message);
    }

    private static MailAccountService CreateService(
        AppDbContext db,
        ICredentialProtector protector,
        FakeConnectivityTester? tester = null,
        IOutboundHostValidator? hosts = null) =>
        new(db, protector, new TestEnvironment("Development"),
            tester ?? new FakeConnectivityTester(), hosts ?? new AllowHostValidator(), NullLogger<MailAccountService>.Instance);

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options);

    private static ICredentialProtector CreateProtector() => new DataProtectionCredentialProtector(
        DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()))));

    private static MailAccountRequest Request(string password) => new(
        "account@example.com", "Account", "account@example.com", password,
        "imap.example.com", 993, MailSecurity.SslOnConnect,
        "smtp.example.com", 587, MailSecurity.StartTls, true);

    private sealed class TestEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "MailClient.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class AllowHostValidator : IOutboundHostValidator
    {
        public HostCheckResult CheckLiteralHost(string? host) => HostCheckResult.Allow();
        public Task<HostCheckResult> CheckAsync(string? host, CancellationToken cancellationToken) =>
            Task.FromResult(HostCheckResult.Allow());
    }

    private sealed class NeverDnsResolver : IDnsResolver
    {
        public Task<System.Net.IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromException<System.Net.IPAddress[]>(new InvalidOperationException("DNS must not be called."));
    }

    private sealed class FakeConnectivityTester : IMailConnectivityTester
    {
        public MailConnectionException? Failure { get; set; }

        public Task TestImapAsync(MailServerEndpoint endpoint, string username, string password, CancellationToken cancellationToken) =>
            Failure is null ? Task.CompletedTask : Task.FromException(Failure);

        public Task TestSmtpAsync(MailServerEndpoint endpoint, string username, string password, CancellationToken cancellationToken) =>
            Failure is null ? Task.CompletedTask : Task.FromException(Failure);
    }
}
