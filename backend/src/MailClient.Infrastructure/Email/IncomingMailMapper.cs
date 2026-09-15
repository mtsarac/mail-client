using MailClient.Domain.Enums;
using MailKit;
using MimeKit;

namespace MailClient.Infrastructure.Email;

public sealed record IncomingMail(
    Guid MailAccountId,
    Guid MailFolderId,
    uint Uid,
    uint UidValidity,
    string MessageId,
    string InReplyToMessageId,
    string References,
    string Subject,
    string FromAddress,
    string FromDisplayName,
    string ToAddress,
    string BodyHtml,
    string BodyText,
    DateTime SentAt,
    DateTime InternalDate,
    bool IsRead,
    bool Answered,
    bool Flagged,
    bool Draft,
    bool Deleted,
    bool Recent,
    IReadOnlyList<IncomingParticipant> Participants,
    IReadOnlyList<IncomingHeader> Headers,
    IReadOnlyList<IncomingAttachment> Attachments);

public sealed record IncomingParticipant(ParticipantType Type, string Address, string DisplayName, int SortOrder);

public sealed record IncomingHeader(string Name, string Value);

public sealed record IncomingAttachment(
    IMimeContent Content,
    string FileName,
    string ContentType,
    bool IsInline,
    string ContentId,
    string ContentDisposition);

public sealed record IncomingFlags(bool IsRead, bool Answered, bool Flagged, bool Draft, bool Deleted, bool Recent);

public static class IncomingMailMapper
{
    public static IncomingMail Map(
        MimeMessage message,
        uint uid,
        uint uidValidity,
        Guid accountId,
        Guid folderId,
        IncomingFlags flags)
    {
        var participants = MapParticipants(message);
        var attachments = message.BodyParts
            .OfType<MimePart>()
            .Select(MapAttachment)
            .OfType<IncomingAttachment>()
            .ToList();

        var from = participants
            .Where(participant => participant.Type == ParticipantType.From)
            .OrderBy(participant => participant.SortOrder)
            .ToList();
        var to = participants
            .Where(participant => participant.Type == ParticipantType.To)
            .OrderBy(participant => participant.SortOrder)
            .ToList();

        var headers = new List<IncomingHeader>();
        foreach (var headerName in (string[])["Content-Language", "List-Id", "List-Unsubscribe"])
        {
            var value = message.Headers[headerName];
            if (!string.IsNullOrWhiteSpace(value))
                headers.Add(new IncomingHeader(headerName, value.Trim()));
        }

        var internalDate = DateTime.UtcNow;

        return new IncomingMail(
            accountId,
            folderId,
            uid,
            uidValidity,
            MailFieldNormalizer.MessageId(message.MessageId),
            MailFieldNormalizer.MessageId(message.InReplyTo),
            MailFieldNormalizer.Truncate(string.Join(' ', message.References), MailFieldLimits.MessageId),
            MailFieldNormalizer.Subject(message.Subject),
            from.Count > 0 ? from[0].Address : string.Empty,
            from.Count > 0 ? from[0].DisplayName : string.Empty,
            to.Count > 0 ? to[0].Address : string.Empty,
            message.HtmlBody ?? string.Empty,
            message.TextBody ?? string.Empty,
            message.Date == DateTimeOffset.MinValue ? internalDate : message.Date.UtcDateTime,
            internalDate,
            flags.IsRead,
            flags.Answered,
            flags.Flagged,
            flags.Draft,
            flags.Deleted,
            flags.Recent,
            participants,
            headers,
            attachments);
    }

    public static List<MailClient.Domain.Entities.MailParticipant> ToEntityParticipants(Guid mailId, IReadOnlyList<IncomingParticipant> participants) =>
        participants
            .Select(participant => new MailClient.Domain.Entities.MailParticipant
            {
                Id = Guid.NewGuid(),
                MailId = mailId,
                Type = participant.Type,
                Address = participant.Address,
                NormalizedAddress = participant.Address.ToLowerInvariant(),
                DisplayName = participant.DisplayName,
                SortOrder = participant.SortOrder
            })
            .ToList();

    private static List<IncomingParticipant> MapParticipants(MimeMessage message)
    {
        var participants = new List<IncomingParticipant>();
        participants.AddRange(MapGroup(ParticipantType.From, message.From.Mailboxes));
        participants.AddRange(MapGroup(ParticipantType.To, message.To.Mailboxes));
        participants.AddRange(MapGroup(ParticipantType.Cc, message.Cc.Mailboxes));
        participants.AddRange(MapGroup(ParticipantType.Bcc, message.Bcc.Mailboxes));
        participants.AddRange(MapGroup(ParticipantType.ReplyTo, message.ReplyTo.Mailboxes));
        return participants;
    }

    private static IEnumerable<IncomingParticipant> MapGroup(ParticipantType type, IEnumerable<MailboxAddress> mailboxes) =>
        mailboxes
            .Where(mailbox => !string.IsNullOrEmpty(mailbox.Address))
            .Select((mailbox, index) => new IncomingParticipant(
                type,
                MailFieldNormalizer.Address(mailbox.Address),
                MailFieldNormalizer.DisplayName(mailbox.Name),
                index));

    private static bool IsInline(MimePart part) => string.Equals(
        part.ContentDisposition?.Disposition,
        ContentDisposition.Inline,
        StringComparison.OrdinalIgnoreCase);

    private static IncomingAttachment? MapAttachment(MimePart part)
    {
        if (part.Content is not { } content
            || !(part.IsAttachment || IsInline(part) || !string.IsNullOrEmpty(part.ContentId)))
            return null;

        var inline = IsInline(part) || (!string.IsNullOrEmpty(part.ContentId) && part.ContentDisposition is null);

        return new IncomingAttachment(
            content,
            MailFieldNormalizer.FileName(part.FileName),
            MailFieldNormalizer.ContentType(part.ContentType.MimeType),
            inline,
            MailFieldNormalizer.ContentId(part.ContentId),
            inline ? ContentDisposition.Inline : ContentDisposition.Attachment);
    }
}
