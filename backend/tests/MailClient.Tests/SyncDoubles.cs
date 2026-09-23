using System.Net;
using MailClient.Application.Accounts;
using MailClient.Application.Mail;
using MailClient.Application.Runtime;
using MailClient.Infrastructure.Runtime;
using MailClient.Application.Sync;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Email;
using MailClient.Infrastructure.Mail;
using MailClient.Infrastructure.Network;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Security;
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
    public HashSet<uint> AnsweredUids { get; } = [];
    public HashSet<uint> FlaggedUids { get; } = [];
    public HashSet<uint> DraftUids { get; } = [];
    public HashSet<uint> DeletedUids { get; } = [];

    /// <summary>0 keeps the legacy oldest-first behaviour; set it to emulate a server that reports UIDNEXT.</summary>
    public uint UidNext { get; set; }
    public long LastBackfillBelowUid { get; private set; }
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

    public Task<UidBackfillResult> SearchOlderAsync(long belowUidExclusive, int maxCount, CancellationToken cancellationToken)
    {
        LastBackfillBelowUid = belowUidExclusive;
        var found = Messages.Keys
            .Where(uid => uid < belowUidExclusive)
            .OrderByDescending(uid => uid)
            .Take(maxCount)
            .OrderBy(uid => uid)
            .Select(uid => new UniqueId(uid))
            .ToList();
        var next = found.Count < maxCount || found[0].Id <= 1 ? 0 : found[0].Id;
        return Task.FromResult(new UidBackfillResult(found, next));
    }

    public Task<IReadOnlyDictionary<uint, RemoteMessageFlags>> GetFlagsAsync(
        IReadOnlyCollection<UniqueId> uids, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<uint, RemoteMessageFlags>>(uids
            .Where(uid => Messages.ContainsKey(uid.Id))
            .ToDictionary(uid => uid.Id, uid => new RemoteMessageFlags(
                _seenUids.Contains(uid.Id),
                AnsweredUids.Contains(uid.Id),
                FlaggedUids.Contains(uid.Id),
                DraftUids.Contains(uid.Id),
                DeletedUids.Contains(uid.Id))));

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
    public List<uint> Expunged { get; } = [];
    public virtual Task<bool> ExpungeAsync(UniqueId uid, CancellationToken cancellationToken)
    {
        Expunged.Add(uid.Id);
        Messages.Remove(uid.Id);
        return Task.FromResult(true);
    }
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

/// <summary>In-memory <see cref="IFileStorage"/> adapter: exercises the storage contract without a real backend.</summary>
public sealed class FakeFileStorage : IFileStorage
{
    private readonly Dictionary<string, byte[]> _content = [];

    public List<string> Saved { get; } = [];
    public List<string> Deleted { get; } = [];
    public bool Available { get; set; } = true;
    public IReadOnlyDictionary<string, byte[]> Content => _content;

    public async Task<StoredFile> SaveAsync(Guid accountId, Guid mailId, Guid attachmentId,
        Func<Stream, CancellationToken, Task> write, long maxBytes, CancellationToken cancellationToken)
    {
        var path = MailClient.Infrastructure.Storage.AttachmentPath.Relative(accountId, mailId, attachmentId);
        using var buffer = new MemoryStream();
        await using (var bounded = new MailClient.Infrastructure.Storage.BoundedWriteStream(buffer, maxBytes))
            await write(bounded, cancellationToken);
        _content[path] = buffer.ToArray();
        Saved.Add(path);
        return new StoredFile(path, _content[path].Length);
    }

    public Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken) =>
        _content.TryGetValue(relativePath, out var bytes)
            ? Task.FromResult<Stream>(new MemoryStream(bytes))
            : throw new FileNotFoundException(relativePath);

    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken)
    {
        Deleted.Add(relativePath);
        _content.Remove(relativePath);
        return Task.CompletedTask;
    }

    public Task DeleteAccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var prefix = MailClient.Infrastructure.Storage.AttachmentPath.AccountPrefix(accountId);
        foreach (var key in _content.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            _content.Remove(key);
            Deleted.Add(key);
        }

        return Task.CompletedTask;
    }

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(Available);
}

internal sealed class FakeSyncScheduler(Action? onSchedule = null) : ISyncScheduler
{
    public List<(Guid AccountId, Guid? FolderId, SyncOrigin Origin)> Scheduled { get; } = [];
    public ValueTask ScheduleAccountAsync(Guid accountId, SyncOrigin origin, CancellationToken cancellationToken)
    {
        Scheduled.Add((accountId, null, origin));
        onSchedule?.Invoke();
        return ValueTask.CompletedTask;
    }

    public ValueTask ScheduleFolderAsync(Guid accountId, Guid folderId, SyncOrigin origin, CancellationToken cancellationToken)
    {
        Scheduled.Add((accountId, folderId, origin));
        onSchedule?.Invoke();
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

internal sealed class FakeMailTransport : MailClient.Infrastructure.Mail.IMailTransport
{
    public int SentCount { get; private set; }
    public int AppendCount { get; private set; }
    public Exception? SendFailure { get; init; }
    public MimeMessage? Message { get; private set; }

    public Task SendAsync(MailAccount account, MimeMessage message, CancellationToken cancellationToken)
    {
        if (SendFailure is not null) throw SendFailure;
        Message = message;
        SentCount++;
        return Task.CompletedTask;
    }

    public Task AppendToSentAsync(MailAccount account, string sentFullName, MimeMessage message, CancellationToken cancellationToken)
    {
        AppendCount++;
        return Task.CompletedTask;
    }
}

internal sealed class DefaultRuntimePolicyProvider : IRuntimePolicyProvider
{
    public Task<RuntimeProviderPolicy> GetAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new RuntimeProviderPolicy(new RuntimeSettings(), new ProviderCapabilities(false, false)));
}

internal sealed class DefaultEmailAllowlistService : IEmailAllowlistService
{
    public Task<bool> IsAllowedAsync(string email, CancellationToken cancellationToken) => Task.FromResult(true);
    public Task<bool> IsEnforcedAsync(CancellationToken cancellationToken) => Task.FromResult(false);
}

/// <summary>Runtime settings for services under test; defaults unless a document is supplied.</summary>
internal sealed class FixedRuntimeSettingsStore(RuntimeSettings? settings = null) : IRuntimeSettingsStore
{
    public Task<RuntimeSettingsSnapshot> GetAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new RuntimeSettingsSnapshot(settings ?? new RuntimeSettings(), 1, DateTime.UtcNow));

    public Task<RuntimeSettingsSnapshot> ReplaceAsync(int expectedVersion, RuntimeSettings replacement, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public static RuntimeOperationSettings Operation(RuntimeSettings? settings = null) => new(new FixedRuntimeSettingsStore(settings));
}

internal static class TestServices
{
    public static MailCredentialResolver Credentials(
        AppDbContext db,
        ICredentialProtector? protector = null,
        IEnumerable<MailClient.Infrastructure.OAuth.IOAuthProvider>? oauthProviders = null,
        MailClient.Infrastructure.Sync.ISyncLockProvider? locks = null,
        IPushNotificationService? push = null,
        MailClient.Application.Observability.MailClientMetrics? metrics = null) =>
        new(db,
            protector ?? new PassthroughProtector(),
            oauthProviders ?? [],
            new DefaultRuntimePolicyProvider(),
            locks ?? new MailClient.Infrastructure.Sync.InMemorySyncLockProvider(),
            push ?? new FakePushNotificationService(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MailCredentialResolver>.Instance,
            metrics);

    public static MailClient.Infrastructure.Sync.InlineFolderSync InlineSync(ISyncExecutor? executor = null) =>
        new(new MailClient.Infrastructure.Sync.InMemorySyncLockProvider(), executor ?? new NoOpSyncExecutor(), new FakeSyncScheduler());

    public static RuntimeSettings SyncSettings(
        int maxMessagesPerRun = 100,
        int flagSyncIntervalSeconds = 120,
        long maxAttachmentBytes = 25 * 1024 * 1024,
        long maxMessageAttachmentBytes = 50 * 1024 * 1024,
        long maxMessageBytes = 100 * 1024 * 1024) => new()
        {
            Sync = new RuntimeSyncSettings { MaxMessagesPerRun = maxMessagesPerRun, FlagSyncIntervalSeconds = flagSyncIntervalSeconds },
            Limits = new RuntimeLimitSettings
            {
                MaxAttachmentBytes = maxAttachmentBytes,
                MaxMessageAttachmentBytes = maxMessageAttachmentBytes,
                MaxMessageBytes = maxMessageBytes
            }
        };
}

internal sealed class NoOpSyncExecutor : ISyncExecutor
{
    public Task SyncFolderAsync(Guid accountId, Guid folderId, CancellationToken cancellationToken) => Task.CompletedTask;
}
