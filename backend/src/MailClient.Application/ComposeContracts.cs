namespace MailClient.Application.Mail;

public enum ComposeMode { Reply, ReplyAll, Forward }

public sealed record ComposeContextResponse(
    Guid SourceMailId,
    IReadOnlyList<MailRecipient> To,
    IReadOnlyList<MailRecipient> Cc,
    string SuggestedSubject,
    string InReplyToMessageId,
    string References,
    string? OriginalFrom,
    string? OriginalDate,
    string? OriginalSubject,
    IReadOnlyList<AttachmentResponse> Attachments);
