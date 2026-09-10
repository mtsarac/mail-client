using MailClient.Infrastructure.Email;
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
}
