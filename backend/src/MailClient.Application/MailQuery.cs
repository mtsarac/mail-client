namespace MailClient.Application.Mail;

public sealed record MailListRequest(
    Guid? FolderId,
    bool? IsRead,
    bool? HasAttachments,
    string? Search,
    int Page,
    int PageSize);

public sealed record MailListItem(
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
    IReadOnlyList<MailListItem> Items,
    int Page,
    int PageSize,
    int Total);
