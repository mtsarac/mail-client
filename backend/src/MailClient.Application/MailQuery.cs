namespace MailClient.Application.Mail;

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

public sealed record MailBodyResponse(
    string Html,
    bool HasRemoteContent,
    IReadOnlyList<string> RemoteContentHosts,
    IReadOnlyList<string> TrackingPixelHosts);

public sealed record MailListItemResponse(
    Guid Id,
    Guid FolderId,
    string Subject,
    string FromAddress,
    string FromDisplayName,
    string ToAddress,
    bool IsRead,
    bool HasAttachments,
    DateTime ReceivedAt,
    Guid? ConversationId = null,
    string Snippet = "",
    bool Flagged = false,
    bool Answered = false,
    int AttachmentCount = 0);

public sealed record MailListResponse(
    IReadOnlyList<MailListItemResponse> Items,
    int Page,
    int PageSize,
    int Total);

public sealed record MailSearchRequest(
    string? Query,
    Guid? FolderId,
    Guid? ConversationId,
    string? From,
    string? To,
    DateTime? FromDate,
    DateTime? ToDate,
    bool? IsRead,
    bool? Flagged,
    bool? HasAttachment,
    int Page,
    int PageSize,
    Guid? LabelId = null);

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
    MailBodyResponse Body,
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
    IReadOnlyList<AttachmentResponse> Attachments,
    Guid? ConversationId = null,
    bool IsFromMe = false);
