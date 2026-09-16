using MailClient.Application.Mail;
using MimeKit;

namespace MailClient.Infrastructure.Email;

public static class MimeMessageBuilder
{
    public static MimeMessage Build(
        string fromAddress,
        string fromDisplayName,
        MailboxAddress to,
        string subject,
        string? bodyHtml,
        string? bodyText,
        IReadOnlyList<SendMailAttachment> attachments) =>
        Build(fromAddress, fromDisplayName, [to], [], [], subject, bodyHtml, bodyText, attachments, null, null);

    public static MimeMessage Build(
        string fromAddress,
        string fromDisplayName,
        IReadOnlyList<MailboxAddress> to,
        IReadOnlyList<MailboxAddress> cc,
        IReadOnlyList<MailboxAddress> bcc,
        string subject,
        string? bodyHtml,
        string? bodyText,
        IReadOnlyList<SendMailAttachment> attachments,
        string? inReplyTo,
        string? references)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(fromDisplayName, fromAddress));
        message.To.AddRange(to);
        message.Cc.AddRange(cc);
        message.Bcc.AddRange(bcc);
        message.Subject = subject;
        message.Date = DateTimeOffset.UtcNow;
        message.MessageId = $"{Guid.NewGuid():N}@mailclient";
        if (!string.IsNullOrWhiteSpace(inReplyTo))
            message.InReplyTo = inReplyTo;
        if (!string.IsNullOrWhiteSpace(references))
            message.References.AddRange(references.Split(' ', StringSplitOptions.RemoveEmptyEntries));

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
