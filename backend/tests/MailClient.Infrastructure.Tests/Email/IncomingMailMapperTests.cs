using MailClient.Domain.Entities;
using MailClient.Infrastructure.Email;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace MailClient.Infrastructure.Tests.Email;

public sealed class IncomingMailMapperTests
{
    [Fact]
    public void Map_UsesSafeFallbacksAndCapturesAttachments()
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Sender", "sender@example.test"));
        var body = new BodyBuilder
        {
            TextBody = "plain",
            HtmlBody = "<p>html</p>"
        };
        body.Attachments.Add("report.txt", [1, 2, 3]);
        message.Body = body.ToMessageBody();
        var accountId = Guid.NewGuid();
        var folderId = Guid.NewGuid();

        var result = IncomingMailMapper.Map(message, 42, 99, accountId, folderId, false);

        Assert.Equal("(no subject)", result.Subject);
        Assert.Equal("sender@example.test", result.FromAddress);
        Assert.Equal("Sender", result.FromDisplayName);
        Assert.Equal(string.Empty, result.ToAddress);
        Assert.Equal("plain", result.BodyText);
        Assert.Equal("<p>html</p>", result.BodyHtml);
        Assert.Equal(42u, result.Uid);
        Assert.Equal(99u, result.UidValidity);
        Assert.Equal(accountId, result.MailAccountId);
        Assert.Equal(folderId, result.MailFolderId);
        Assert.False(result.IsRead);
        Assert.Single(result.Attachments);
        Assert.Equal("report.txt", result.Attachments[0].FileName);
    }

    [Fact]
    public void Map_TruncatesOverlongSubjectToLimit()
    {
        var message = new MimeMessage { Subject = new string('s', MailFieldLimits.Subject + 50) };
        message.From.Add(new MailboxAddress("Sender", "sender@example.test"));
        message.Body = new TextPart("plain") { Text = "hello" };

        var result = IncomingMailMapper.Map(message, 1, 1, Guid.NewGuid(), Guid.NewGuid(), false);

        Assert.Equal(MailFieldLimits.Subject, result.Subject.Length);
    }

    [Fact]
    public void Map_TruncatesOverlongSenderDisplayNameToLimit()
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(new string('n', MailFieldLimits.FromDisplayName + 20), "sender@example.test"));
        message.Body = new TextPart("plain") { Text = "hello" };

        var result = IncomingMailMapper.Map(message, 1, 1, Guid.NewGuid(), Guid.NewGuid(), false);

        Assert.Equal(MailFieldLimits.FromDisplayName, result.FromDisplayName.Length);
    }

    [Fact]
    public void Map_TruncatesOverlongMessageIdToLimit()
    {
        var message = new MimeMessage { MessageId = new string('m', MailFieldLimits.MessageId + 10) };
        message.From.Add(new MailboxAddress("Sender", "sender@example.test"));
        message.Body = new TextPart("plain") { Text = "hello" };

        var result = IncomingMailMapper.Map(message, 1, 1, Guid.NewGuid(), Guid.NewGuid(), false);

        Assert.Equal(MailFieldLimits.MessageId, result.MessageId.Length);
    }

    [Fact]
    public void Map_TruncatesOverlongAttachmentFilenameToLimit()
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Sender", "sender@example.test"));
        var body = new BodyBuilder { TextBody = "hi" };
        body.Attachments.Add(new string('f', MailFieldLimits.FileName + 30) + ".txt", [1, 2, 3]);
        message.Body = body.ToMessageBody();

        var result = IncomingMailMapper.Map(message, 1, 1, Guid.NewGuid(), Guid.NewGuid(), false);

        Assert.Single(result.Attachments);
        Assert.Equal(MailFieldLimits.FileName, result.Attachments[0].FileName.Length);
    }

    [Fact]
    public void Map_HandlesNullAndWhitespaceValues()
    {
        var message = new MimeMessage();
        message.Body = new TextPart("plain") { Text = "hello" };

        var result = IncomingMailMapper.Map(message, 1, 1, Guid.NewGuid(), Guid.NewGuid(), false);

        Assert.Equal("(no subject)", result.Subject);
        Assert.Equal(string.Empty, result.FromAddress);
        Assert.Equal(string.Empty, result.FromDisplayName);
        Assert.True(result.MessageId.Length <= MailFieldLimits.MessageId);
    }

    [Fact]
    public void FieldLimits_MatchEfCoreModelMaxLengths()
    {
        using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var model = db.Model;
        Assert.Equal(MailFieldLimits.Subject, MaxLength<Mail>(model, nameof(Mail.Subject)));
        Assert.Equal(MailFieldLimits.FromAddress, MaxLength<Mail>(model, nameof(Mail.FromAddress)));
        Assert.Equal(MailFieldLimits.FromDisplayName, MaxLength<Mail>(model, nameof(Mail.FromDisplayName)));
        Assert.Equal(MailFieldLimits.ToAddress, MaxLength<Mail>(model, nameof(Mail.ToAddress)));
        Assert.Equal(MailFieldLimits.MessageId, MaxLength<Mail>(model, nameof(Mail.MessageId)));
        Assert.Equal(MailFieldLimits.FileName, MaxLength<Attachment>(model, nameof(Attachment.FileName)));
        Assert.Equal(MailFieldLimits.ContentType, MaxLength<Attachment>(model, nameof(Attachment.ContentType)));
        Assert.Equal(MailFieldLimits.ContentId, MaxLength<Attachment>(model, nameof(Attachment.ContentId)));
    }

    private static int? MaxLength<T>(Microsoft.EntityFrameworkCore.Metadata.IModel model, string property)
        where T : class =>
        model.FindEntityType(typeof(T))?.FindProperty(property)?.GetMaxLength();
}
