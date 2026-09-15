namespace MailClient.Application.Mail;

public sealed record MailListRequest(
    Guid? FolderId,
    bool? IsRead,
    bool? HasAttachments,
    string? Search,
    int Page,
    int PageSize);

public sealed record MailParticipantResponse(
    Guid Id,
    string Type,
    string Address,
    string DisplayName,
    int SortOrder);

public sealed record AttachmentResponse(
    Guid Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    bool IsInline,
    string ContentId,
    string ContentDisposition);

public sealed record MailHeaderResponse(string Name, string Value);

public sealed record MailListItemResponse(
    Guid Id,
    Guid FolderId,
    string Subject,
    string FromAddress,
    string FromDisplayName,
    string ToAddress,
    bool IsRead,
    bool HasAttachments,
    DateTime ReceivedAt);

public sealed record MailListResponse(
    IReadOnlyList<MailListItemResponse> Items,
    int Page,
    int PageSize,
    int Total);

public sealed record MailDetailResponse(
    Guid Id,
    Guid FolderId,
    Guid AccountId,
    uint Uid,
    string MessageId,
    string InReplyToMessageId,
    string References,
    string Subject,
    IReadOnlyList<MailParticipantResponse> From,
    IReadOnlyList<MailParticipantResponse> To,
    IReadOnlyList<MailParticipantResponse> Cc,
    IReadOnlyList<MailParticipantResponse> Bcc,
    IReadOnlyList<MailParticipantResponse> ReplyTo,
    string BodyText,
    string BodyHtml,
    bool IsRead,
    bool Answered,
    bool Flagged,
    bool Draft,
    bool Deleted,
    bool Recent,
    bool HasAttachments,
    DateTime SentAt,
    DateTime ReceivedAt,
    DateTime InternalDate,
    IReadOnlyList<MailHeaderResponse> Headers,
    IReadOnlyList<AttachmentResponse> Attachments);
