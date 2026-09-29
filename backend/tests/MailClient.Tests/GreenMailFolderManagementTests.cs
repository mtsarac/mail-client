using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Mail;
using MailClient.Infrastructure.Observability;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Services;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using MimeKit;

namespace MailClient.Tests;

[Collection("greenmail")]
public sealed class GreenMailFolderManagementTests(GreenMailFixture greenmail) : IAsyncLifetime
{
    private readonly string _databaseName = $"mailclient_folders_{Guid.NewGuid():N}";
    private string _connectionString = "";

    public async Task InitializeAsync()
    {
        if (!IntegrationEnvironment.GreenMailEnabled || !IntegrationEnvironment.PostgresEnabled)
            return;
        await using (var admin = new NpgsqlConnection(IntegrationEnvironment.PostgresAdmin))
        {
            await admin.OpenAsync();
            await using var command = admin.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{_databaseName}\"";
            await command.ExecuteNonQueryAsync();
        }
        _connectionString = new NpgsqlConnectionStringBuilder(IntegrationEnvironment.PostgresAdmin) { Database = _databaseName }.ConnectionString;
        await using var db = CreateDb();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrEmpty(_connectionString)) return;
        await using var admin = new NpgsqlConnection(IntegrationEnvironment.PostgresAdmin);
        await admin.OpenAsync();
        await using var command = admin.CreateCommand();
        command.CommandText = $"DROP DATABASE \"{_databaseName}\" WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task FolderLifecycle_IsRemoteFirst_PreservesNestedIds_AndRefusesNonemptyDelete()
    {
        if (!IntegrationEnvironment.GreenMailEnabled || !IntegrationEnvironment.PostgresEnabled) return;
        var accountId = Guid.NewGuid();
        await using var db = CreateDb();
        var account = new MailAccount
        {
            Id = accountId,
            EmailAddress = $"{accountId:N}@localhost",
            NormalizedEmailAddress = $"{accountId:N}@LOCALHOST",
            Username = greenmail.Username,
            ImapHost = greenmail.Host,
            ImapPort = greenmail.ImapPort,
            ImapSecurity = MailSecurity.StartTls,
            SmtpHost = greenmail.Host,
            SmtpPort = 3025
        };
        db.MailAccounts.Add(account);
        db.MailCredentials.Add(new MailCredential
        {
            Id = Guid.NewGuid(),
            MailAccountId = accountId,
            AuthenticationMethod = AuthenticationMethod.Password,
            EncryptedMaterial = greenmail.Password,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var remote = new DirectRemoteManager(greenmail);
        var service = new FolderManagementService(db, remote, new FakeFileStorage(), new AuditLogger(db));
        var rootName = $"Phase6-{Guid.NewGuid():N}";
        var root = await service.CreateAsync(accountId, null, rootName, "test", CancellationToken.None);
        Assert.Null(root.Error);
        Assert.Equal(rootName, root.Folder!.Name);
        Assert.False(string.IsNullOrEmpty(root.Folder.Delimiter));
        var child = await service.CreateAsync(accountId, root.Folder.Id, "Mobil", "test", CancellationToken.None);
        Assert.Null(child.Error);
        Assert.Equal(root.Folder.Id, MailFolderHierarchy.ParentIds(await db.MailFolders.ToListAsync())[child.Folder!.Id]);
        Assert.Equal("mail_folder_has_children", await service.DeleteAsync(accountId, root.Folder.Id, "test", CancellationToken.None));

        var renamed = await service.RenameAsync(accountId, root.Folder.Id, rootName + "-new", "test", CancellationToken.None);
        Assert.Null(renamed.Error);
        Assert.Equal(child.Folder.Id, (await db.MailFolders.SingleAsync(folder => folder.Name == "Mobil")).Id);
        Assert.StartsWith(rootName + "-new" + root.Folder.Delimiter, child.Folder.FullName, StringComparison.Ordinal);
        using (var imap = await ConnectAsync())
        {
            Assert.NotNull(await imap.GetFolderAsync(child.Folder.FullName));
            Assert.NotNull(await imap.GetFolderAsync(root.Folder.FullName));
            await imap.DisconnectAsync(true);
        }

        using (var imap = await ConnectAsync())
        {
            var seedTarget = await imap.GetFolderAsync(child.Folder.FullName);
            await seedTarget.AppendAsync(new MimeMessage
            {
                From = { new MailboxAddress("Sender", "sender@example.test") },
                To = { new MailboxAddress("Recipient", greenmail.Username) },
                Subject = "Do not delete",
                Body = new TextPart("plain") { Text = "body" }
            });
            await imap.DisconnectAsync(true);
        }
        var destination = await service.CreateAsync(accountId, null, rootName + "-destination", "test", CancellationToken.None);
        Assert.Null(destination.Error);
        var moved = await service.MoveAsync(accountId, root.Folder.Id, destination.Folder!.Id, "test", CancellationToken.None);
        Assert.Null(moved.Error);
        Assert.Equal(root.Folder.Id, moved.Folder!.Id);
        Assert.Equal(destination.Folder.Id, MailFolderHierarchy.ParentIds(await db.MailFolders.ToListAsync())[root.Folder.Id]);
        Assert.Equal(root.Folder.Id, MailFolderHierarchy.ParentIds(await db.MailFolders.ToListAsync())[child.Folder.Id]);
        using (var imap = await ConnectAsync())
        {
            var remoteChild = await imap.GetFolderAsync(child.Folder.FullName);
            await remoteChild.StatusAsync(StatusItems.Count);
            Assert.Equal(1, remoteChild.Count);
            await imap.DisconnectAsync(true);
        }
        // GreenMail 2.1.8 acknowledges RENAME of a nested folder to a top-level name but drops it,
        // so moving back to the root is covered with a fake remote in FolderManagementApiTests.
        Assert.Equal("mail_folder_has_children", await service.DeleteAsync(accountId, destination.Folder.Id, "test", CancellationToken.None));
        Assert.Equal("mail_folder_not_empty", await service.DeleteAsync(accountId, child.Folder.Id, "test", CancellationToken.None));
        using (var imap = await ConnectAsync())
        {
            var cleanupTarget = await imap.GetFolderAsync(child.Folder.FullName);
            await cleanupTarget.OpenAsync(FolderAccess.ReadWrite);
            var uids = await cleanupTarget.SearchAsync(MailKit.Search.SearchQuery.All);
            await cleanupTarget.AddFlagsAsync(uids, MessageFlags.Deleted, true);
            await cleanupTarget.CloseAsync(true);
            await imap.DisconnectAsync(true);
        }
        Assert.Null(await service.DeleteAsync(accountId, child.Folder.Id, "test", CancellationToken.None));
        Assert.Null(await service.DeleteAsync(accountId, root.Folder.Id, "test", CancellationToken.None));
        Assert.Null(await service.DeleteAsync(accountId, destination.Folder.Id, "test", CancellationToken.None));
        Assert.False(await db.MailFolders.AnyAsync(folder => folder.MailAccountId == accountId));
        using var verify = await ConnectAsync();
        await Assert.ThrowsAsync<FolderNotFoundException>(() => verify.GetFolderAsync(root.Folder.FullName));
        await verify.DisconnectAsync(true);
    }

    private AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_connectionString).Options);

    private async Task<ImapClient> ConnectAsync()
    {
        var imap = new ImapClient();
        await imap.ConnectAsync(greenmail.Host, greenmail.ImapPort, SecureSocketOptions.None);
        await imap.AuthenticateAsync(greenmail.Username, greenmail.Password);
        return imap;
    }
    private sealed class DirectRemoteManager(GreenMailFixture greenmail) : IRemoteFolderManager
    {
        public Task<RemoteFolderResult> CreateAsync(MailAccount account, string? parentFullName, string name, CancellationToken ct) =>
            RunAsync((imap, token) => MailKitRemoteFolderManager.CreateAsync(imap, parentFullName, name, token), ct);

        public Task<RemoteFolderResult> RenameAsync(MailAccount account, string fullName, string name, CancellationToken ct) =>
            RunAsync((imap, token) => MailKitRemoteFolderManager.RenameAsync(imap, fullName, name, token), ct);

        public Task<RemoteFolderResult> MoveAsync(MailAccount account, string fullName, string? parentFullName, CancellationToken ct) =>
            RunAsync((imap, token) => MailKitRemoteFolderManager.MoveAsync(imap, fullName, parentFullName, token), ct);

        public Task<RemoteFolderResult> DeleteAsync(MailAccount account, string fullName, CancellationToken ct) =>
            RunAsync((imap, token) => MailKitRemoteFolderManager.DeleteAsync(imap, fullName, token), ct);

        private async Task<RemoteFolderResult> RunAsync(Func<ImapClient, CancellationToken, Task<RemoteFolderResult>> action, CancellationToken ct)
        {
            using var imap = new ImapClient();
            await imap.ConnectAsync(greenmail.Host, greenmail.ImapPort, SecureSocketOptions.None, ct);
            await imap.AuthenticateAsync(greenmail.Username, greenmail.Password, ct);
            var result = await action(imap, ct);
            await imap.DisconnectAsync(true, ct);
            return result;
        }
    }
}
