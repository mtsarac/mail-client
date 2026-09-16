namespace MailClient.Application.Mail;

public sealed record DraftCommand(
    Guid AccountId,
    IReadOnlyList<string> To,
    IReadOnlyList<string> Cc,
    IReadOnlyList<string> Bcc,
    string Subject,
    string? BodyHtml,
    string? BodyText,
    IReadOnlyList<SendMailAttachment> Attachments,
    Guid? ReplySourceMailId);

public enum DraftLookupError
{
    None,
    NotFound,
    NotDraft,
    DraftsFolderUnavailable
}

public sealed record DraftLookupResult(MailDetailResponse? Draft, DraftLookupError Error)
{
    public bool Found => Draft is not null;
}

public sealed record DraftWriteResult(bool Created, Guid? MailId, bool ReconciliationPending, string? Warning);

public sealed record DraftDeleteResult(bool Deleted, string? Warning);
public sealed record DraftSendResult(bool Sent, bool SentCopySaved, bool DraftRemoved, string? Warning);
