using MailClient.Application.Interfaces;

// Traversal-pinned local disk storage for attachment files.
namespace MailClient.Infrastructure.Storage;

public sealed class LocalFileStorage(string rootPath) : IFileStorage
{
    public async Task<StoredFile> SaveAsync(
        Guid accountId,
        Guid mailId,
        Guid attachmentId,
        Func<Stream, CancellationToken, Task> write,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var relativePath = Path.Combine(
            "attachments",
            accountId.ToString("N"),
            mailId.ToString("N"),
            attachmentId.ToString("N"));
        var destination = Path.Combine(rootPath, relativePath);
        var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            await using (var file = File.Create(temporary))
            await using (var bounded = new BoundedWriteStream(file, maxBytes))
                await write(bounded, cancellationToken);
            File.Move(temporary, destination);
            return new StoredFile(relativePath, new FileInfo(destination).Length);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
    }

    public Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stream stream = new FileStream(
            ScopedPath(relativePath), FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, useAsync: true);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(ScopedPath(relativePath));
        return Task.CompletedTask;
    }

    public Task DeleteAccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var accountDir = ScopedPath(Path.Combine("attachments", accountId.ToString("N")));
        if (Directory.Exists(accountDir))
            Directory.Delete(accountDir, recursive: true);
        return Task.CompletedTask;
    }

    private string ScopedPath(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(rootPath, relativePath));
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.Ordinal))
            throw new InvalidOperationException("Attachment path escapes the storage root.");
        return full;
    }
}
