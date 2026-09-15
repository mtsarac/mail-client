namespace MailClient.Application.Mail;

public enum MailOperationKind { Read, Unread, Star, Unstar, Move, Copy, Trash, Restore, Archive, Spam, NotSpam }
public enum MailOperationError { None, NotFound, FolderNotFound, NeedsReauthentication, ProviderUnavailable, Conflict, MoveFailed, NotSupported }
public sealed record MailOperationRequest(Guid MailId, MailOperationKind Kind, Guid? DestinationFolderId = null);
public sealed record MailOperationResult(bool Success, MailOperationError Error = MailOperationError.None, bool DestinationReconciliationRequired = false);
public interface IMailOperationService
{
    Task<MailOperationResult> ExecuteAsync(Guid accountId, MailOperationRequest request, string? correlationId, CancellationToken cancellationToken);
}
