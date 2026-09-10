namespace MailClient.Application.Interfaces;

public interface IFileStorage
{
    Task<StoredFile> SaveAsync(
        Guid accountId,
        Guid mailId,
        Guid attachmentId,
        Func<Stream, CancellationToken, Task> write,
        long maxBytes,
        CancellationToken cancellationToken);

    Task DeleteAsync(string relativePath, CancellationToken cancellationToken);

    // Read-only stream for downloads. The caller owns disposal.
    Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken);

    // Path is derived from accountId only. Idempotent.
    Task DeleteAccountAsync(Guid accountId, CancellationToken cancellationToken);
}

public sealed record StoredFile(string RelativePath, long SizeBytes);
