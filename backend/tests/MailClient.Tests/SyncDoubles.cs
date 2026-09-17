using System.Net;
using MailClient.Application.Accounts;
using MailClient.Application.Mail;
using MailClient.Application.Sync;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Email;
using MailClient.Infrastructure.Mail;
using MailClient.Infrastructure.Network;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using MailKit;
using MimeKit;

namespace MailClient.Tests;

internal sealed class FakeDns(params IPAddress[] addresses) : IDnsResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) => Task.FromResult(addresses);
}

internal class FakeRemoteMailFolder(
    uint uidValidity,
    Dictionary<uint, Func<MimeMessage>> messages,
    Dictionary<uint, uint>? sizes = null,
    HashSet<uint>? missingSizes = null,
    HashSet<uint>? seenUids = null) : IRemoteMailFolder
{
    public Dictionary<uint, Func<MimeMessage>> Messages { get; } = messages;
    public Dictionary<uint, Func<Exception>> Failures { get; } = [];
    public int LastSearchMaxCount { get; private set; }
    public uint LastSearchAfterUid { get; private set; }
    public uint UidValidity { get; set; } = uidValidity;
    private readonly Dictionary<uint, uint> _sizes = sizes ?? [];
    private readonly HashSet<uint> _missingSizes = missingSizes ?? [];
    private readonly HashSet<uint> _seenUids = seenUids ?? [];

    public Task OpenAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task OpenForUpdateAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<UidSearchResult> SearchNewAsync(uint afterUid, int maxCount, CancellationToken cancellationToken)
    {
        LastSearchMaxCount = maxCount;
        LastSearchAfterUid = afterUid;
        var found = Messages.Keys
            .Where(uid => uid > afterUid)
            .OrderBy(uid => uid)
            .Take(maxCount)
            .Select(uid => new UniqueId(uid))
            .ToList();
        return Task.FromResult(new UidSearchResult(found, found.Count == 0 ? afterUid : found.Max(uid => uid.Id)));
    }

    public Task<IReadOnlyDictionary<uint, RemoteSummary?>> GetSummariesAsync(
        IReadOnlyCollection<UniqueId> uids, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<uint, RemoteSummary?>>(uids
            .Where(uid => !_missingSizes.Contains(uid.Id))
            .ToDictionary(
                uid => uid.Id,
                uid => (RemoteSummary?)new RemoteSummary(
                    _sizes.GetValueOrDefault(uid.Id, 100u),
                    _seenUids.Contains(uid.Id),
                    IsAnswered: false,
                    IsFlagged: false,
                    IsDraft: false,
                    IsDeleted: false,
                    IsRecent: false,
                    InternalDate: null)));

    public Task<IReadOnlyDictionary<uint, bool>> GetFlagsAsync(
        IReadOnlyCollection<UniqueId> uids, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<uint, bool>>(uids
            .Where(uid => Messages.ContainsKey(uid.Id))
            .ToDictionary(uid => uid.Id, uid => _seenUids.Contains(uid.Id)));

    public Task<MimeMessage> GetMessageAsync(UniqueId uid, CancellationToken cancellationToken)
    {
        if (Failures.TryGetValue(uid.Id, out var makeFailure))
            throw makeFailure();
        return Task.FromResult(Messages[uid.Id]());
    }

    public Task SetSeenAsync(UniqueId uid, bool seen, CancellationToken cancellationToken)
    {
        if (seen)
            _seenUids.Add(uid.Id);
        else
            _seenUids.Remove(uid.Id);
        return Task.CompletedTask;
    }

    public virtual Task SetFlaggedAsync(UniqueId uid, bool flagged, CancellationToken cancellationToken) => Task.CompletedTask;
    public virtual Task<RemoteMoveResult> MoveAsync(UniqueId uid, string destinationFullName, CancellationToken cancellationToken)
    {
        Moved.Add(uid.Id);
        return Task.FromResult(new RemoteMoveResult(null, UidValidity));
    }
    public Task<RemoteMoveResult> CopyAsync(UniqueId uid, string destinationFullName, CancellationToken cancellationToken) => Task.FromResult(new RemoteMoveResult(null, UidValidity));
    public RemoteAppendResult AppendResult { get; set; } = new(null, uidValidity);
    public MessageFlags? LastAppendFlags { get; private set; }
    public string? LastFolderName { get; set; }
    public Exception? AppendFailure { get; set; }
    public List<uint> Moved { get; } = [];

    public Task<RemoteAppendResult> AppendAsync(MimeMessage message, MessageFlags flags, CancellationToken cancellationToken)
    {
        if (AppendFailure is not null) throw AppendFailure;
        LastAppendFlags = flags;
        return Task.FromResult(AppendResult);
    }
}

internal sealed class FakeMailFolderClient(FakeRemoteMailFolder remote) : IMailFolderClient
{
    public Task<T> UseFolderAsync<T>(
        MailAccount account,
        string fullName,
        bool forUpdate,
        Func<IRemoteMailFolder, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken) =>
        action(remote, cancellationToken);
}

internal sealed class FakeFileStorage : IFileStorage
{
    public List<string> Saved { get; } = [];
    public List<string> Deleted { get; } = [];

    public Task<StoredFile> SaveAsync(Guid accountId, Guid mailId, Guid attachmentId,
        Func<Stream, CancellationToken, Task> write, long maxBytes, CancellationToken cancellationToken)
    {
        var path = $"fake/{Guid.NewGuid():N}";
        Saved.Add(path);
        return Task.FromResult(new StoredFile(path, 3));
    }

    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken)
    {
        Deleted.Add(relativePath);
        return Task.CompletedTask;
    }
}

internal sealed class FakeSyncScheduler : ISyncScheduler
{
    public List<(Guid AccountId, Guid? FolderId, SyncOrigin Origin)> Scheduled { get; } = [];
    public ValueTask ScheduleAccountAsync(Guid accountId, SyncOrigin origin, CancellationToken cancellationToken)
    {
        Scheduled.Add((accountId, null, origin));
        return ValueTask.CompletedTask;
    }

    public ValueTask ScheduleFolderAsync(Guid accountId, Guid folderId, SyncOrigin origin, CancellationToken cancellationToken)
    {
        Scheduled.Add((accountId, folderId, origin));
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeSyncClock(DateTime now) : ISyncClock
{
    private readonly List<TimeSpan> _delays = [];
    public DateTime Current { get; set; } = now;
    public IReadOnlyList<TimeSpan> Delays => _delays;
    public DateTime UtcNow => Current;
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _delays.Add(delay);
        Current += delay;
        return Task.CompletedTask;
    }
}

internal sealed class FakePushNotificationService : IPushNotificationService
{
    public List<PushEvent> Notifications { get; } = [];
    public Task NotifyAsync(PushEvent pushEvent, CancellationToken cancellationToken)
    {
        Notifications.Add(pushEvent);
        return Task.CompletedTask;
    }
}

internal sealed class PassthroughProtector : ICredentialProtector
{
    public string Protect(string plaintext) => plaintext;
    public string Unprotect(string protectedValue) => protectedValue;
}

internal static class SyncTestSeed
{
    public static async Task<(Guid AccountId, Guid FolderId)> SeedFolderAsync(AppDbContext db)
    {
        var accountId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        db.MailAccounts.Add(new MailAccount
        {
            Id = accountId,
            EmailAddress = "a@example.test",
            NormalizedEmailAddress = "A@EXAMPLE.TEST",
            Username = "a",
            ImapHost = "imap.example.test",
            ImapPort = 993,
            SmtpHost = "smtp.example.test",
            SmtpPort = 587,
            Status = MailAccountStatus.Active
        });
        db.MailFolders.Add(new Domain.Entities.MailFolder
        {
            Id = folderId,
            MailAccountId = accountId,
            Name = "INBOX",
            FullName = "INBOX",
            FolderType = MailFolderType.Inbox,
            IsSyncEnabled = true,
            IsAvailable = true
        });
        db.SyncStates.Add(new SyncState { Id = Guid.NewGuid(), MailAccountId = accountId, MailFolderId = folderId, UidValidity = 7, LastUid = 0 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (accountId, folderId);
    }

    public static MimeMessage SimpleMessage(string subject)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Sender", "sender@example.test"));
        message.To.Add(new MailboxAddress("Receiver", "receiver@example.test"));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = "hello" };
        return message;
    }
}
