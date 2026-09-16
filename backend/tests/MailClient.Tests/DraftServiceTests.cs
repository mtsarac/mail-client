using MailClient.Application.Mail;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Email;
using MailClient.Infrastructure.Mail;
using MailClient.Infrastructure.Observability;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Services;
using MailClient.Application.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

namespace MailClient.Tests;

public sealed class DraftServiceTests
{
    [Fact]
    public async Task CreateAsync_UsesDiscoveredDraftsFolderWithDraftFlag()
    {
        await using var db = CreateDb();
        var (accountId, draftsId) = await SeedAsync(db);
        var remote = new FakeRemoteMailFolder(31, new()) { AppendResult = new(new MailKit.UniqueId(8), 31) };
        var sync = new RecordingSyncExecutor();
        var service = CreateService(db, remote, sync);

        var result = await service.CreateAsync(accountId, Command(accountId), null, CancellationToken.None);

        Assert.True(result.Created);
        Assert.Equal("Drafts", remote.LastFolderName);
        Assert.Equal(MailKit.MessageFlags.Draft, remote.LastAppendFlags);
        Assert.Equal(draftsId, sync.FolderId);
    }

    [Fact]
    public async Task UpdateAsync_AppendFailure_LeavesOriginalDraftUntouched()
    {
        await using var db = CreateDb();
        var (accountId, draftsId) = await SeedAsync(db);
        var draftId = await SeedDraftAsync(db, accountId, draftsId);
        var remote = new FakeRemoteMailFolder(31, new()) { AppendFailure = new InvalidOperationException("append_failed") };
        var service = CreateService(db, remote, new RecordingSyncExecutor());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateAsync(accountId, draftId, Command(accountId), null, CancellationToken.None));

        Assert.NotNull(await db.Mails.SingleOrDefaultAsync(mail => mail.Id == draftId));
        Assert.Empty(remote.Moved);
    }

    [Fact]
    public async Task GetAsync_ForeignAccountAndNonDraft_AreRejected()
    {
        await using var db = CreateDb();
        var (accountId, draftsId) = await SeedAsync(db);
        var mailId = await SeedDraftAsync(db, accountId, draftsId, draft: false);
        var service = CreateService(db, new FakeRemoteMailFolder(31, new()), new RecordingSyncExecutor());

        var other = await service.GetAsync(Guid.NewGuid(), mailId, CancellationToken.None);
        var nonDraft = await service.GetAsync(accountId, mailId, CancellationToken.None);

        Assert.Equal(DraftLookupError.NotFound, other.Error);
        Assert.Equal(DraftLookupError.NotDraft, nonDraft.Error);
    }

    private static DraftCommand Command(Guid accountId) => new(
        accountId,
        ["to@example.test"],
        [],
        ["bcc@example.test"],
        "subject",
        "<p>html</p>",
        "text",
        [],
        null);

    private static DraftService CreateService(AppDbContext db, FakeRemoteMailFolder remote, RecordingSyncExecutor sync)
    {
        var folders = new RecordingMailFolderClient(remote);
        var audit = new AuditLogger(db);
        var reader = new MailReadService(db, folders, audit, NullLogger<MailReadService>.Instance);
        var operations = new MailOperationService(db, folders, reader, audit, new InitialSyncQueue(), NullLogger<MailOperationService>.Instance);
        return new(db, folders, sync, reader, operations, audit, NullLogger<DraftService>.Instance);
    }

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static async Task<(Guid AccountId, Guid DraftsId)> SeedAsync(AppDbContext db)
    {
        var accountId = Guid.NewGuid();
        var draftsId = Guid.NewGuid();
        db.MailAccounts.Add(new MailAccount
        {
            Id = accountId,
            EmailAddress = "me@example.test",
            NormalizedEmailAddress = $"ME-{accountId:N}@EXAMPLE.TEST",
            DisplayName = "Me",
            Username = "me",
            Status = MailAccountStatus.Active
        });
        db.MailFolders.Add(new MailFolder { Id = draftsId, MailAccountId = accountId, Name = "Drafts", FullName = "Drafts", FolderType = MailFolderType.Drafts, IsAvailable = true, IsSyncEnabled = true, UidValidity = 31 });
        await db.SaveChangesAsync();
        return (accountId, draftsId);
    }

    private static async Task<Guid> SeedDraftAsync(AppDbContext db, Guid accountId, Guid folderId, bool draft = true)
    {
        var id = Guid.NewGuid();
        db.Mails.Add(new MailClient.Domain.Entities.Mail { Id = id, MailAccountId = accountId, MailFolderId = folderId, Uid = 5, UidValidity = 31, Draft = draft, MessageId = "draft@example.test", Subject = "old" });
        await db.SaveChangesAsync();
        return id;
    }

    private sealed class RecordingSyncExecutor : ISyncExecutor
    {
        public Guid? FolderId { get; private set; }
        public Task SyncAccountAsync(Guid accountId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SyncFolderAsync(Guid accountId, Guid folderId, CancellationToken cancellationToken)
        {
            FolderId = folderId;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingMailFolderClient(FakeRemoteMailFolder remote) : IMailFolderClient
    {
        public Task<T> UseFolderAsync<T>(MailAccount account, string fullName, bool forUpdate, Func<IRemoteMailFolder, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
        {
            remote.LastFolderName = fullName;
            return action(remote, cancellationToken);
        }
    }
}
