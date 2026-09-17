using System.Net;
using System.Text.Json;
using MailClient.Application.Accounts;
using MailClient.Application.Authentication;
using MailClient.Application.Mail;
using MailClient.Application.Runtime;
using MailClient.Application.Sync;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Accounts;
using MailClient.Infrastructure.Authentication;
using MailClient.Infrastructure.Mail;
using MailClient.Infrastructure.Network;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Security;
using MailClient.Infrastructure.Services;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MailClient.Tests;

/// <summary>
/// Records the authentication method handed to MailKit. OAuth accounts must reach IMAP through XOAUTH2;
/// presenting an access token as a password fails against Google/Microsoft and marks accounts for reauthentication.
/// </summary>
internal sealed class RecordingMailConnectionHelper() : MailConnectionHelper(
    new OutboundHostValidator(new FakeDns(IPAddress.Loopback)),
    NullLogger<MailConnectionHelper>.Instance)
{
    public List<(string Operation, AuthenticationMethod Method, string Secret)> ImapCalls { get; } = [];
    public List<(string Operation, AuthenticationMethod Method)> SmtpCalls { get; } = [];

    public override Task<T> WithImapAsync<T>(
        MailServerEndpoint endpoint,
        string username,
        string password,
        string operation,
        Func<ImapClient, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken,
        AuthenticationMethod authenticationMethod = AuthenticationMethod.Password)
    {
        ImapCalls.Add((operation, authenticationMethod, password));
        return Task.FromResult(Empty<T>());
    }

    public override Task<T> WithSmtpAsync<T>(
        MailServerEndpoint endpoint,
        string username,
        string password,
        string operation,
        Func<SmtpClient, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken,
        AuthenticationMethod authenticationMethod = AuthenticationMethod.Password)
    {
        SmtpCalls.Add((operation, authenticationMethod));
        return Task.FromResult(Empty<T>());
    }

    private static T Empty<T>() =>
        typeof(T) == typeof(IReadOnlyList<DiscoveredMailFolder>)
            ? (T)(object)Array.Empty<DiscoveredMailFolder>()
            : default!;
}

public sealed class AuthenticationPropagationTests
{
    [Theory]
    [InlineData(AuthenticationMethod.OAuth2)]
    [InlineData(AuthenticationMethod.Password)]
    public async Task FolderSync_UsesTheResolvedAuthenticationMethod(AuthenticationMethod method)
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedAccountAsync(db, method);
        var connections = new RecordingMailConnectionHelper();
        var service = CreateSyncService(db, connections);

        await service.SyncFolderAsync(accountId, folderId, CancellationToken.None);

        var call = Assert.Single(connections.ImapCalls);
        Assert.Equal("SyncFolder", call.Operation);
        Assert.Equal(method, call.Method);
    }

    [Theory]
    [InlineData(AuthenticationMethod.OAuth2)]
    [InlineData(AuthenticationMethod.Password)]
    public async Task FolderRefresh_UsesTheResolvedAuthenticationMethod(AuthenticationMethod method)
    {
        await using var db = CreateDb();
        var (accountId, _) = await SeedAccountAsync(db, method);
        var connections = new RecordingMailConnectionHelper();

        await CreateConnectionService(db, connections).RefreshFoldersAsync(accountId, CancellationToken.None);

        var call = Assert.Single(connections.ImapCalls);
        Assert.Equal("ExploreFolders", call.Operation);
        Assert.Equal(method, call.Method);
    }

    [Fact]
    public async Task OAuthAccount_SendsAccessTokenOnlyOverXoauth2()
    {
        await using var db = CreateDb();
        var (accountId, folderId) = await SeedAccountAsync(db, AuthenticationMethod.OAuth2);
        var connections = new RecordingMailConnectionHelper();

        await CreateSyncService(db, connections).SyncFolderAsync(accountId, folderId, CancellationToken.None);

        var call = Assert.Single(connections.ImapCalls);
        Assert.Equal("access-token", call.Secret);
        Assert.Equal(AuthenticationMethod.OAuth2, call.Method);
    }

    [Fact]
    public async Task ManualConnect_DiscoversFoldersWithTheStoredCredential()
    {
        await using var db = CreateDb();
        var connections = new RecordingMailConnectionHelper();
        var service = CreateConnectionService(db, connections, new StoreMarkingProtector());

        await service.ConnectManualAsync(
            new ManualConnectRequest(
                "person@example.test",
                "person@example.test",
                new AuthenticationInput(AuthenticationMethod.AppSpecificPassword, "app-password"),
                new EndpointInput("imap.example.test", 993, MailSecurity.SslOnConnect),
                new EndpointInput("smtp.example.test", 465, MailSecurity.SslOnConnect)),
            CancellationToken.None);

        var call = Assert.Single(connections.ImapCalls);
        Assert.Equal("ExploreFolders", call.Operation);
        Assert.Equal(AuthenticationMethod.AppSpecificPassword, call.Method);
        // The marker is only added when the secret is read back out of the credential store, so folder discovery
        // authenticated with the persisted credential rather than with the values carried on the request.
        Assert.Equal("app-password|from-store", call.Secret);
    }

    private static MailFolderSyncService CreateSyncService(AppDbContext db, MailConnectionHelper connections) =>
        new(db,
            new MailCredentialResolver(db, new PassthroughProtector()),
            connections,
            new FakeFileStorage(),
            new MailSyncOptions(),
            new FakePushNotificationService(),
            new ConversationService(db),
            new MailReconciliationService(db),
            NullLogger<MailFolderSyncService>.Instance);

    private static AccountConnectionService CreateConnectionService(
        AppDbContext db,
        MailConnectionHelper connections,
        ICredentialProtector? protector = null)
    {
        protector ??= new PassthroughProtector();
        return new AccountConnectionService(db,
            new StubMailValidator(true),
            protector,
            new MailSessionService(db, new SessionOptions()),
            new MailClient.Api.Auth.JwtTokenIssuer(new MailClient.Api.Auth.JwtOptions("i", "a", new string('k', 40))),
            new FakeSyncScheduler(),
            new MailKitFolderExplorer(connections),
            new MailCredentialResolver(db, protector),
            new DefaultRuntimePolicyProvider(),
            NullLogger<AccountConnectionService>.Instance);
    }

    private static async Task<(Guid AccountId, Guid FolderId)> SeedAccountAsync(AppDbContext db, AuthenticationMethod method)
    {
        var accountId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        db.MailAccounts.Add(new MailAccount
        {
            Id = accountId,
            EmailAddress = "person@example.test",
            NormalizedEmailAddress = "PERSON@EXAMPLE.TEST",
            Username = "person@example.test",
            Provider = method == AuthenticationMethod.OAuth2 ? MailProvider.Google : MailProvider.Custom,
            AuthenticationMethod = method,
            ImapHost = "imap.example.test",
            ImapPort = 993,
            SmtpHost = "smtp.example.test",
            SmtpPort = 465,
            Status = MailAccountStatus.Active
        });
        db.MailCredentials.Add(new MailCredential
        {
            Id = Guid.NewGuid(),
            MailAccountId = accountId,
            AuthenticationMethod = method,
            Provider = method == AuthenticationMethod.OAuth2 ? MailProvider.Google : MailProvider.Custom,
            EncryptedMaterial = method == AuthenticationMethod.OAuth2
                ? JsonSerializer.Serialize(new OAuthCredentialMaterial("access-token", "refresh-token"))
                : "password",
            ExpiresAt = method == AuthenticationMethod.OAuth2 ? DateTime.UtcNow.AddHours(1) : null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        db.MailFolders.Add(new MailClient.Domain.Entities.MailFolder
        {
            Id = folderId,
            MailAccountId = accountId,
            Name = "INBOX",
            FullName = "INBOX",
            FolderType = MailFolderType.Inbox,
            IsSyncEnabled = true,
            IsAvailable = true
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (accountId, folderId);
    }

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
}

/// <summary>
/// Marks secrets on the way out of the store so a test can tell a stored credential apart from the plaintext a
/// request carried.
/// </summary>
internal sealed class StoreMarkingProtector : ICredentialProtector
{
    public string Protect(string plaintext) => plaintext;
    public string Unprotect(string protectedValue) => protectedValue + "|from-store";
}
