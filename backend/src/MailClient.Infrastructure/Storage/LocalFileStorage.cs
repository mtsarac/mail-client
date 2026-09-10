using MailClient.Application.Interfaces;

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

    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(Path.Combine(rootPath, relativePath));
        return Task.CompletedTask;
    }
}
