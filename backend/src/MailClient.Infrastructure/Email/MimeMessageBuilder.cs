using MailClient.Application.Interfaces;
using MimeKit;

// Builds outgoing MIME messages with normalized attachments for sending.
namespace MailClient.Infrastructure.Email;

internal static class MimeMessageBuilder
{
    public static MimeMessage Build(
        string fromAddress,
        string fromDisplayName,
        MailboxAddress to,
        string subject,
        string? bodyHtml,
        string? bodyText,
        IReadOnlyList<SendMailAttachment> attachments)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(fromDisplayName, fromAddress));
        message.To.Add(to);
        message.Subject = subject;
        message.Date = DateTimeOffset.UtcNow;
        message.MessageId = $"{Guid.NewGuid():N}@mailclient";

        MimeEntity body = (bodyHtml, bodyText) switch
        {
            ({ Length: > 0 } html, { Length: > 0 } text) => new MultipartAlternative
            {
                new TextPart("plain") { Text = text },
                new TextPart("html") { Text = html }
            },
            ({ Length: > 0 } html, _) => new TextPart("html") { Text = html },
            _ => new TextPart("plain") { Text = bodyText ?? string.Empty }
        };

        if (attachments.Count == 0)
        {
            message.Body = body;
            return message;
        }

        var mixed = new Multipart("mixed") { body };
        foreach (var attachment in attachments)
            mixed.Add(ToPart(attachment));
        message.Body = mixed;
        return message;
    }

    private static MimePart ToPart(SendMailAttachment attachment)
    {
        ContentType contentType;
        try
        {
            contentType = ContentType.Parse(MailFieldNormalizer.ContentType(attachment.ContentType));
        }
        catch (ParseException)
        {
            contentType = new ContentType("application", "octet-stream");
        }

        return new MimePart(contentType)
        {
            Content = new MimeContent(attachment.Content),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            ContentTransferEncoding = ContentEncoding.Base64,
            FileName = MailFieldNormalizer.FileName(attachment.FileName)
        };
    }
}
