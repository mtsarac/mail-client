using MailClient.Application;
using MailClient.Application.Interfaces;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Services;
using MailClient.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MailClient.Infrastructure.Tests.Services;

public sealed class MailQueryServiceTests
{
    [Fact]
    public async Task List_ReturnsOnlyOwnMail()
    {
        await using var db = CreateDb();
        var owner = await SeedUserAsync(db);
        var other = await SeedUserAsync(db);
        var folder = await SeedFolderAsync(db, owner, MailFolderType.Inbox);
        await SeedMailAsync(db, owner, folder, subject: "mine");
        var otherFolder = await SeedFolderAsync(db, other, MailFolderType.Inbox);
        await SeedMailAsync(db, other, otherFolder, subject: "theirs");

        var page = await Service(db).ListAsync(owner, new MailListQuery(null, null, null, 1, 30), CancellationToken.None);

        Assert.Equal(ServiceOutcome.Ok, page.Outcome);
        Assert.Single(page.Value!.Items);
        Assert.Equal("mine", page.Value.Items[0].Subject);
    }

    [Fact]
    public async Task List_FolderTypeFilter_ReturnsUnifiedInbox()
    {
        await using var db = CreateDb();
        var owner = await SeedUserAsync(db);
        var inboxA = await SeedFolderAsync(db, owner, MailFolderType.Inbox, accountEmail: "a@example.test");
        var inboxB = await SeedFolderAsync(db, owner, MailFolderType.Inbox, accountEmail: "b@example.test");
        var sent = await SeedFolderAsync(db, owner, MailFolderType.Sent);
        var baseTime = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        await SeedMailAsync(db, owner, inboxA, subject: "in-a", receivedAt: baseTime);
        await SeedMailAsync(db, owner, inboxB, subject: "in-b", receivedAt: baseTime.AddMinutes(1));
        await SeedMailAsync(db, owner, sent, subject: "sent", receivedAt: baseTime.AddMinutes(2));

        var page = await Service(db).ListAsync(
            owner, new MailListQuery(null, null, MailFolderType.Inbox, 1, 30), CancellationToken.None);

        Assert.Equal(2, page.Value!.TotalCount);
        Assert.Equal("in-b", page.Value.Items[0].Subject);
        Assert.Equal("in-a", page.Value.Items[1].Subject);
    }

    [Fact]
    public async Task List_AccountAndFolderFilters_ScopeCorrectly()
    {
        await using var db = CreateDb();
        var owner = await SeedUserAsync(db);
        var inbox = await SeedFolderAsync(db, owner, MailFolderType.Inbox, accountEmail: "a@example.test");
        var accountId = (await db.MailFolders.SingleAsync(f => f.Id == inbox)).MailAccountId;
        var sentId = Guid.NewGuid();
        db.MailFolders.Add(new MailFolder
        {
            Id = sentId,
            MailAccountId = accountId,
            Name = "Sent",
            FullName = "Sent",
            FolderType = MailFolderType.Sent,
            IsSyncEnabled = true
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await SeedMailAsync(db, owner, inbox, subject: "in");
        await SeedMailAsync(db, owner, sentId, subject: "sent");

        var byAccount = await Service(db).ListAsync(
            owner, new MailListQuery(accountId, null, null, 1, 30), CancellationToken.None);
        Assert.Equal(2, byAccount.Value!.TotalCount);

        var byFolder = await Service(db).ListAsync(
            owner, new MailListQuery(null, sentId, null, 1, 30), CancellationToken.None);
        Assert.Single(byFolder.Value!.Items);
        Assert.Equal("sent", byFolder.Value.Items[0].Subject);

        var missingAccount = await Service(db).ListAsync(
            owner, new MailListQuery(Guid.NewGuid(), null, null, 1, 30), CancellationToken.None);
        Assert.Equal(ServiceOutcome.NotFound, missingAccount.Outcome);

        var missingFolder = await Service(db).ListAsync(
            owner, new MailListQuery(null, Guid.NewGuid(), null, 1, 30), CancellationToken.None);
        Assert.Equal(ServiceOutcome.NotFound, missingFolder.Outcome);
    }

    [Fact]
    public async Task List_PaginationAndStableOrdering()
    {
        await using var db = CreateDb();
        var owner = await SeedUserAsync(db);
        var folder = await SeedFolderAsync(db, owner, MailFolderType.Inbox);
        var sameTime = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var first = await SeedMailAsync(db, owner, folder, subject: "first", receivedAt: sameTime);
        var second = await SeedMailAsync(db, owner, folder, subject: "second", receivedAt: sameTime);
        await SeedMailAsync(db, owner, folder, subject: "newest", receivedAt: sameTime.AddHours(1));
        var expectedSecondFirst = second.CompareTo(first) > 0 ? "second" : "first";

        var page1 = await Service(db).ListAsync(owner, new MailListQuery(null, null, null, 1, 2), CancellationToken.None);
        Assert.Equal(3, page1.Value!.TotalCount);
        Assert.Equal("newest", page1.Value.Items[0].Subject);
        Assert.Equal(expectedSecondFirst, page1.Value.Items[1].Subject);

        var page2 = await Service(db).ListAsync(owner, new MailListQuery(null, null, null, 2, 2), CancellationToken.None);
        Assert.Single(page2.Value!.Items);
        Assert.Equal(expectedSecondFirst == "second" ? "first" : "second", page2.Value.Items[0].Subject);
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public async Task List_InvalidPagination_Rejected(int page, int pageSize)
    {
        await using var db = CreateDb();
        var owner = await SeedUserAsync(db);

        var result = await Service(db).ListAsync(
            owner, new MailListQuery(null, null, null, page, pageSize), CancellationToken.None);

        Assert.Equal(ServiceOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public async Task Detail_EnforcesOwnershipAndExposesBodyWithAttachmentMetadata()
    {
        await using var db = CreateDb();
        var owner = await SeedUserAsync(db);
        var admin = await SeedUserAsync(db, role: UserRole.Admin);
        var folder = await SeedFolderAsync(db, owner, MailFolderType.Inbox);
        var mailId = await SeedMailAsync(db, owner, folder, subject: "detail",
            bodyHtml: "<p>hi</p>", bodyText: "hi");
        var attachmentId = Guid.NewGuid();
        db.Attachments.Add(new Attachment
        {
            Id = attachmentId,
            MailId = mailId,
            FileName = "doc.pdf",
            ContentType = "application/pdf",
            SizeBytes = 11,
            StoragePath = "attachments/fake",
            IsInline = false,
            ContentId = string.Empty
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var detail = await Service(db).GetDetailAsync(owner, mailId, CancellationToken.None);
        Assert.Equal(ServiceOutcome.Ok, detail.Outcome);
        Assert.Equal("<p>hi</p>", detail.Value!.BodyHtml);
        Assert.Equal("hi", detail.Value.BodyText);
        Assert.Single(detail.Value.Attachments);
        Assert.Equal("doc.pdf", detail.Value.Attachments[0].FileName);
        Assert.Equal("application/pdf", detail.Value.Attachments[0].ContentType);

        Assert.Equal(ServiceOutcome.NotFound,
            (await Service(db).GetDetailAsync(Guid.NewGuid(), mailId, CancellationToken.None)).Outcome);
        Assert.Equal(ServiceOutcome.NotFound,
            (await Service(db).GetDetailAsync(admin, mailId, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Attachment_DownloadsOwnedBytes_RejectsWrongChain()
    {
        await using var db = CreateDb();
        var owner = await SeedUserAsync(db);
        var folder = await SeedFolderAsync(db, owner, MailFolderType.Inbox);
        var mailId = await SeedMailAsync(db, owner, folder, subject: "with-file");
        var otherMailId = await SeedMailAsync(db, owner, folder, subject: "other");
        var attachmentId = Guid.NewGuid();
        var bytes = new byte[1024 * 64];
        new Random(7).NextBytes(bytes);
        db.Attachments.Add(new Attachment
        {
            Id = attachmentId,
            MailId = mailId,
            FileName = "blob.bin",
            ContentType = "application/octet-stream",
            SizeBytes = bytes.Length,
            StoragePath = "attachments/blob",
            IsInline = false,
            ContentId = string.Empty
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var storage = new FakeFileStorage();
        storage.Files["attachments/blob"] = bytes;

        var result = await new MailQueryService(db, storage, NullLogger<MailQueryService>.Instance)
            .GetAttachmentAsync(owner, mailId, attachmentId, CancellationToken.None);

        Assert.Equal(ServiceOutcome.Ok, result.Outcome);
        await using var content = result.Value!.Content;
        Assert.Equal("blob.bin", result.Value.FileName);
        var downloaded = await content.ReadToEndAsync();
        Assert.Equal(bytes, downloaded);

        var crossUser = await Service(db).GetAttachmentAsync(Guid.NewGuid(), mailId, attachmentId, CancellationToken.None);
        Assert.Equal(ServiceOutcome.NotFound, crossUser.Outcome);

        var wrongMail = await Service(db).GetAttachmentAsync(owner, otherMailId, attachmentId, CancellationToken.None);
        Assert.Equal(ServiceOutcome.NotFound, wrongMail.Outcome);
    }

    [Fact]
    public async Task Attachment_MissingPhysicalFile_ReturnsNotFound()
    {
        await using var db = CreateDb();
        var owner = await SeedUserAsync(db);
        var folder = await SeedFolderAsync(db, owner, MailFolderType.Inbox);
        var mailId = await SeedMailAsync(db, owner, folder, subject: "ghost");
        var attachmentId = Guid.NewGuid();
        db.Attachments.Add(new Attachment
        {
            Id = attachmentId,
            MailId = mailId,
            FileName = "ghost.bin",
            ContentType = "application/octet-stream",
            SizeBytes = 3,
            StoragePath = "attachments/ghost",
            IsInline = false,
            ContentId = string.Empty
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await Service(db).GetAttachmentAsync(owner, mailId, attachmentId, CancellationToken.None);

        Assert.Equal(ServiceOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task Storage_OpenRead_RoundTripsAndRejectsTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailclient-openread-{Guid.NewGuid():N}");
        try
        {
            var storage = new LocalFileStorage(root);
            var stored = await storage.SaveAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                (destination, ct) => new MemoryStream("attachment-bytes"u8.ToArray()).CopyToAsync(destination, ct),
                1024, CancellationToken.None);

            await using var stream = await storage.OpenReadAsync(stored.RelativePath, CancellationToken.None);
            Assert.Equal("attachment-bytes"u8.ToArray(), await stream.ReadToEndAsync());

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                storage.OpenReadAsync(Path.Combine("..", "escape"), CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static MailQueryService Service(AppDbContext db, IFileStorage? storage = null) =>
        new(db, storage ?? new FakeFileStorage(), NullLogger<MailQueryService>.Instance);

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static async Task<Guid> SeedUserAsync(AppDbContext db, UserRole role = UserRole.User)
    {
        var userId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = userId,
            Email = $"mail-{userId:N}@example.test",
            PasswordHash = "seed",
            DisplayName = "Mail",
            Role = role
        });
        await db.SaveChangesAsync();
        return userId;
    }

    private static async Task<Guid> SeedFolderAsync(
        AppDbContext db, Guid userId, MailFolderType type, string? accountEmail = null)
    {
        var accountId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        db.MailAccounts.Add(new MailAccount
        {
            Id = accountId,
            UserId = userId,
            EmailAddress = accountEmail ?? $"box-{accountId:N}@example.test",
            DisplayName = "Box",
            Username = "box",
            EncryptedPassword = "x",
            ImapHost = "imap.example.test",
            ImapPort = 993,
            SmtpHost = "smtp.example.test",
            SmtpPort = 587
        });
        db.MailFolders.Add(new MailFolder
        {
            Id = folderId,
            MailAccountId = accountId,
            Name = type.ToString(),
            FullName = type.ToString(),
            FolderType = type,
            IsSyncEnabled = true
        });
        await db.SaveChangesAsync();
        return folderId;
    }

    private static async Task<Guid> SeedMailAsync(
        AppDbContext db, Guid userId, Guid folderId,
        string subject, DateTime? receivedAt = null,
        string bodyHtml = "", string bodyText = "body")
    {
        var folder = await db.MailFolders.SingleAsync(item => item.Id == folderId);
        var mailId = Guid.NewGuid();
        db.Mails.Add(new Mail
        {
            Id = mailId,
            MailAccountId = folder.MailAccountId,
            MailFolderId = folderId,
            Uid = (uint)Random.Shared.Next(1, 1000000),
            UidValidity = 7,
            MessageId = $"{mailId:N}@example.test",
            Subject = subject,
            FromAddress = "sender@example.test",
            FromDisplayName = "Sender",
            ToAddress = "me@example.test",
            BodyHtml = bodyHtml,
            BodyText = bodyText,
            ReceivedAt = receivedAt ?? DateTime.UtcNow,
            IsRead = false
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return mailId;
    }
}

internal static class StreamExtensions
{
    public static async Task<byte[]> ReadToEndAsync(this Stream stream, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }
}
