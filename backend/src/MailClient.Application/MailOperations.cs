namespace MailClient.Application.Mail;

public enum MailOperationKind { Read, Unread, Star, Unstar, Move, Copy, Trash, Restore, Archive, Spam, NotSpam }
public enum MailOperationError { None, NotFound, FolderNotFound, NeedsReauthentication, ProviderUnavailable, Conflict, MoveFailed, NotSupported }
public sealed record MailOperationRequest(Guid MailId, MailOperationKind Kind, Guid? DestinationFolderId = null);
public sealed record MailOperationResult(bool Success, MailOperationError Error = MailOperationError.None, bool DestinationReconciliationRequired = false)
{
    public bool ReconciliationPending => DestinationReconciliationRequired;
}
public sealed record BulkMailOperationItemResult(Guid MailId, bool Success, MailOperationError Error = MailOperationError.None);
public sealed record BulkMailOperationResult(IReadOnlyList<BulkMailOperationItemResult> Results);
public interface IMailOperationService
{
    Task<MailOperationResult> ExecuteAsync(Guid accountId, MailOperationRequest request, string? correlationId, CancellationToken cancellationToken);

    /// <summary>Applies the same operation to multiple mails, one at a time, so a failure on one mail
    /// (a conflict, a missing folder) never blocks the rest of the batch.</summary>
    Task<BulkMailOperationResult> ExecuteBulkAsync(Guid accountId, IReadOnlyList<Guid> mailIds, MailOperationKind kind, Guid? destinationFolderId, string? correlationId, CancellationToken cancellationToken);
}
