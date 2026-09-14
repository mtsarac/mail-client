using MimeKit;

// Maps downloaded MIME messages to cached mail and attachment records.
namespace MailClient.Infrastructure.Email;

internal sealed record IncomingMail(
    Guid MailAccountId,
    Guid MailFolderId,
    uint Uid,
    uint UidValidity,
    string MessageId,
    string Subject,
    string FromAddress,
    string FromDisplayName,
    string ToAddress,
    string BodyHtml,
    string BodyText,
    DateTime ReceivedAt,
    bool IsRead,
    IReadOnlyList<IncomingAttachment> Attachments);

internal sealed record IncomingAttachment(
    IMimeContent Content,
    string FileName,
    string ContentType,
    bool IsInline,
    string ContentId);

internal static class IncomingMailMapper
{
    public static IncomingMail Map(
        MimeMessage message,
        uint uid,
        uint uidValidity,
        Guid accountId,
        Guid folderId,
        bool isRead)
    {
        var from = message.From.Mailboxes.FirstOrDefault();
        var to = message.To.Mailboxes.FirstOrDefault();
        var attachments = message.BodyParts
            .OfType<MimePart>()
            .Select(MapAttachment)
            .OfType<IncomingAttachment>()
            .ToList();

        return new IncomingMail(
            accountId,
            folderId,
            uid,
            uidValidity,
            MailFieldNormalizer.MessageId(message.MessageId),
            MailFieldNormalizer.Subject(message.Subject),
            MailFieldNormalizer.Address(from?.Address),
            MailFieldNormalizer.DisplayName(from?.Name),
            MailFieldNormalizer.ToAddress(to?.Address),
            message.HtmlBody ?? string.Empty,
            message.TextBody ?? string.Empty,
            message.Date == DateTimeOffset.MinValue ? DateTime.UtcNow : message.Date.UtcDateTime,
            isRead,
            attachments);
    }

    private static bool IsInline(MimePart part) => string.Equals(
        part.ContentDisposition?.Disposition,
        ContentDisposition.Inline,
        StringComparison.OrdinalIgnoreCase);

    private static IncomingAttachment? MapAttachment(MimePart part)
    {
        if (part.Content is not { } content || !(part.IsAttachment || (IsInline(part) && !string.IsNullOrEmpty(part.ContentId))))
            return null;

        return new IncomingAttachment(
            content,
            MailFieldNormalizer.FileName(part.FileName),
            MailFieldNormalizer.ContentType(part.ContentType.MimeType),
            IsInline(part),
            MailFieldNormalizer.ContentId(part.ContentId));
    }
}
