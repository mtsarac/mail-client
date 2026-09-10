using MailClient.Application.Interfaces;
using MailClient.Infrastructure.Email;
using MimeKit;

namespace MailClient.Infrastructure.Tests.Email;

public sealed class MimeMessageBuilderTests
{
    [Fact]
    public void HtmlAndText_WithAttachment_BuildsMixedWithAlternative()
    {
        using var content = new MemoryStream("file-bytes"u8.ToArray());
        var message = MimeMessageBuilder.Build(
            "me@example.test", "Me",
            new MailboxAddress("You", "you@example.test"),
            "Subject", "<p>hi</p>", "hi",
            [new SendMailAttachment("doc.txt", "text/plain", content)]);

        Assert.Equal("me@example.test", message.From.Mailboxes.Single().Address);
        Assert.Equal("you@example.test", message.To.Mailboxes.Single().Address);
        Assert.Equal("Subject", message.Subject);
        Assert.False(string.IsNullOrWhiteSpace(message.MessageId));
        Assert.NotEqual(default, message.Date);
        var mixed = Assert.IsType<Multipart>(message.Body);
        Assert.Equal("mixed", mixed.ContentType.MediaSubtype);
        var alternative = Assert.IsType<MultipartAlternative>(mixed[0]);
        Assert.Equal("hi", alternative.OfType<TextPart>().First(part => part.IsPlain).Text);
        Assert.Equal("<p>hi</p>", alternative.OfType<TextPart>().First(part => part.IsHtml).Text);
        var attachment = Assert.IsType<MimePart>(mixed[1]);
        Assert.Equal("doc.txt", attachment.FileName);
        Assert.Equal("text/plain", attachment.ContentType.MimeType);
    }

    [Fact]
    public void TextOnly_BuildsSinglePlainPart()
    {
        var message = MimeMessageBuilder.Build(
            "me@example.test", "Me",
            new MailboxAddress("You", "you@example.test"),
            "Subject", null, "hello",
            []);

        var part = Assert.IsType<TextPart>(message.Body);
        Assert.True(part.IsPlain);
        Assert.Equal("hello", part.Text);
    }

    [Fact]
    public void HtmlOnly_BuildsSingleHtmlPart()
    {
        var message = MimeMessageBuilder.Build(
            "me@example.test", "Me",
            new MailboxAddress("You", "you@example.test"),
            "Subject", "<p>hello</p>", null,
            []);

        var part = Assert.IsType<TextPart>(message.Body);
        Assert.True(part.IsHtml);
    }

    [Fact]
    public void InvalidAttachmentContentType_FallsBackToOctetStream()
    {
        using var content = new MemoryStream([1, 2, 3]);
        var message = MimeMessageBuilder.Build(
            "me@example.test", "Me",
            new MailboxAddress("You", "you@example.test"),
            "Subject", null, "hello",
            [new SendMailAttachment("blob", "not a mime type", content)]);

        var mixed = Assert.IsType<Multipart>(message.Body);
        var attachment = Assert.IsType<MimePart>(mixed[1]);
        Assert.Equal("application/octet-stream", attachment.ContentType.MimeType);
    }
}
