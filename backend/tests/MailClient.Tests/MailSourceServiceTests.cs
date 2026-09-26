using System.Text;
using MailClient.Application.Mail;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

namespace MailClient.Tests;

public sealed class MailSourceServiceTests
{
    [Fact]
    public async Task Source_UsesOriginalImapContentAndEnforcesAccountScope()
    {
        await using var db = CreateDb();
        var (accountId, mailId) = await SeedAsync(db);
        var original = new MimeMessage();
        original.From.Add(MailboxAddress.Parse("Alice <alice@example.test>"));
        original.To.Add(MailboxAddress.Parse("Bob <bob@example.test>"));
        original.Subject = "Original subject";
        original.Headers.Add("X-Trace", "unsaved diagnostic header");
        original.Body = new TextPart("plain") { Text = "MIME body not in cache" };
        var remote = new FakeRemoteMailFolder(7, new Dictionary<uint, Func<MimeMessage>> { [5] = () => original });
        var service = new MailSourceService(db, new FakeMailFolderClient(remote), NullLogger<MailSourceService>.Instance);

        var headers = await service.GetHeadersAsync(accountId, mailId, CancellationToken.None);
        var raw = await service.GetRawAsync(accountId, mailId, CancellationToken.None);
        var denied = await service.GetRawAsync(Guid.NewGuid(), mailId, CancellationToken.None);

        Assert.Contains(headers.Value!.Headers, header => header.Name == "X-Trace" && header.Value == "unsaved diagnostic header");
        Assert.Contains("MIME body not in cache", Encoding.UTF8.GetString(raw.Value!));
        Assert.Null(denied.Value);
        Assert.Equal(MailOperationError.NotFound, denied.Error);
    }

    [Fact]
    public async Task Source_RejectsStaleUidValidity()
    {
        await using var db = CreateDb();
        var (accountId, mailId) = await SeedAsync(db);
        var remote = new FakeRemoteMailFolder(8, new Dictionary<uint, Func<MimeMessage>> { [5] = () => new MimeMessage() });
        var service = new MailSourceService(db, new FakeMailFolderClient(remote), NullLogger<MailSourceService>.Instance);

        var result = await service.GetHeadersAsync(accountId, mailId, CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Equal(MailOperationError.Conflict, result.Error);
    }

    private static async Task<(Guid AccountId, Guid MailId)> SeedAsync(AppDbContext db)
    {
        var accountId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        var mailId = Guid.NewGuid();
        db.MailAccounts.Add(new MailAccount
        {
            Id = accountId,
            EmailAddress = "bob@example.test",
            NormalizedEmailAddress = "BOB@EXAMPLE.TEST",
            Username = "bob",
            ImapHost = "imap.example.test",
            ImapPort = 993,
            SmtpHost = "smtp.example.test",
            SmtpPort = 587,
            Status = MailAccountStatus.Active
        });
        db.MailFolders.Add(new MailFolder
        {
            Id = folderId,
            MailAccountId = accountId,
            Name = "INBOX",
            FullName = "INBOX",
            FolderType = MailFolderType.Inbox,
            IsSyncEnabled = true,
            IsAvailable = true
        });
        db.Mails.Add(new MailClient.Domain.Entities.Mail
        {
            Id = mailId,
            MailAccountId = accountId,
            MailFolderId = folderId,
            Uid = 5,
            UidValidity = 7,
            Subject = "cached"
        });
        await db.SaveChangesAsync();
        return (accountId, mailId);
    }

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
