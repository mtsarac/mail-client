using MailClient.Application.Interfaces;
using MailClient.Domain.Entities;
using MailClient.Infrastructure.Email;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Storage;
using MailKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Update;
using MimeKit;

namespace MailClient.Infrastructure.Tests.Services;

internal sealed class FakeRemoteMailFolder(
    uint uidValidity,
    Dictionary<uint, Func<MimeMessage>> messages,
    Dictionary<uint, uint>? sizes = null,
    HashSet<uint>? missingSizes = null) : IRemoteMailFolder
{
    public Dictionary<uint, Func<MimeMessage>> Messages { get; } = messages;
    public Dictionary<uint, Func<Exception>> Failures { get; } = [];
    public Dictionary<uint, int> GetMessageCalls { get; } = [];
    public int GetSizesCalls { get; private set; }
    public uint UidValidity { get; } = uidValidity;
    private readonly Dictionary<uint, uint> _sizes = sizes ?? [];
    private readonly HashSet<uint> _missingSizes = missingSizes ?? [];

    public int MessageCallsFor(uint uid) => GetMessageCalls.GetValueOrDefault(uid, 0);

    public Task OpenAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IList<UniqueId>> SearchNewAsync(uint afterUid, CancellationToken cancellationToken) =>
        Task.FromResult<IList<UniqueId>>(Messages.Keys
            .Where(uid => uid > afterUid)
            .OrderBy(uid => uid)
            .Select(uid => new UniqueId(uid))
            .ToList());

    public Task<IReadOnlyDictionary<uint, uint?>> GetSizesAsync(
        IReadOnlyCollection<UniqueId> uids, CancellationToken cancellationToken)
    {
        GetSizesCalls++;
        return Task.FromResult<IReadOnlyDictionary<uint, uint?>>(uids
            .Where(uid => !_missingSizes.Contains(uid.Id))
            .ToDictionary(uid => uid.Id, uid => (uint?)_sizes.GetValueOrDefault(uid.Id, 100u)));
    }

    public Task<MimeMessage> GetMessageAsync(UniqueId uid, CancellationToken cancellationToken)
    {
        GetMessageCalls[uid.Id] = MessageCallsFor(uid.Id) + 1;
        if (Failures.TryGetValue(uid.Id, out var makeFailure))
            throw makeFailure();
        var message = Messages[uid.Id]();
        foreach (var part in message.BodyParts.OfType<MimePart>())
            if (part.FileName is not null)
                (FakeFileStorage.TestAttachmentName.Queue.Value ??= new Queue<string>()).Enqueue(part.FileName);
        return Task.FromResult(message);
    }
}

internal sealed class FakeFileStorage(
    string[]? rejectFileNames = null,
    string[]? failFileNames = null) : IFileStorage
{
    public List<string> Saved { get; } = [];
    public List<string> Deleted { get; } = [];
    public List<Guid> DeletedAccounts { get; } = [];

    public Task<StoredFile> SaveAsync(Guid accountId, Guid mailId, Guid attachmentId,
        Func<Stream, CancellationToken, Task> write, long maxBytes, CancellationToken cancellationToken)
    {
        var fileName = NextFileName();
        if (failFileNames?.Contains(fileName) == true)
            throw new IOException("disk failed");
        if (rejectFileNames?.Contains(fileName) == true)
            throw new AttachmentLimitExceededException();
        var path = $"fake/{Guid.NewGuid():N}";
        Saved.Add(path);
        return Task.FromResult(new StoredFile(path, 3));
    }

    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken)
    {
        Deleted.Add(relativePath);
        return Task.CompletedTask;
    }

    public Task DeleteAccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        DeletedAccounts.Add(accountId);
        return Task.CompletedTask;
    }

    private static string? NextFileName()
    {
        var queue = TestAttachmentName.Queue.Value;
        return queue is { Count: > 0 } ? queue.Dequeue() : null;
    }

    public static class TestAttachmentName
    {
        public static AsyncLocal<Queue<string>?> Queue { get; } = new();
    }
}

internal sealed class FailNextSaveDbContext(DbContextOptions<AppDbContext> options) : AppDbContext(options)
{
    public bool FailNext { get; set; }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (FailNext)
        {
            FailNext = false;
            throw new DbUpdateException("transient database failure", new List<IUpdateEntry>());
        }

        return base.SaveChangesAsync(cancellationToken);
    }
}

internal static class SyncTestSeed
{
    public static async Task<(Guid AccountId, Guid FolderId)> SeedFolderAsync(AppDbContext db)
    {
        var userId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = userId,
            Email = $"seed-{userId:N}@example.test",
            PasswordHash = "seed",
            DisplayName = "Seed"
        });
        db.MailAccounts.Add(new MailAccount
        {
            Id = accountId,
            UserId = userId,
            EmailAddress = "a@example.test",
            DisplayName = "A",
            Username = "a",
            EncryptedPassword = "x",
            ImapHost = "imap.example.test",
            ImapPort = 993,
            SmtpHost = "smtp.example.test",
            SmtpPort = 587
        });
        db.MailFolders.Add(new MailClient.Domain.Entities.MailFolder
        {
            Id = folderId,
            MailAccountId = accountId,
            Name = "INBOX",
            FullName = "INBOX",
            IsSyncEnabled = true
        });
        db.SyncStates.Add(new SyncState { Id = Guid.NewGuid(), MailFolderId = folderId, UidValidity = 7, LastUid = 0 });
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

    public static MimeMessage MessageWithAttachment(string fileName)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Sender", "sender@example.test"));
        message.To.Add(new MailboxAddress("Receiver", "receiver@example.test"));
        message.Subject = "with attachment";
        var body = new BodyBuilder { TextBody = "see attached" };
        body.Attachments.Add(fileName, [1, 2, 3]);
        message.Body = body.ToMessageBody();
        return message;
    }

    public static MimeMessage MessageWithTwoAttachments(string firstFileName, string secondFileName)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Sender", "sender@example.test"));
        message.To.Add(new MailboxAddress("Receiver", "receiver@example.test"));
        message.Subject = "with attachments";
        var body = new BodyBuilder { TextBody = "see attached" };
        body.Attachments.Add(firstFileName, [1, 2, 3]);
        body.Attachments.Add(secondFileName, [4, 5, 6]);
        message.Body = body.ToMessageBody();
        return message;
    }
}
