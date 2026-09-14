namespace MailClient.Infrastructure.Storage;

public sealed class LocalAttachmentStorage(string rootPath)
{
    public string Resolve(string relativePath)
    {
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!path.StartsWith(root, StringComparison.Ordinal)) throw new InvalidOperationException("Attachment path escapes storage root.");
        return path;
    }

    public Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<Stream>(new FileStream(Resolve(relativePath), FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true));
    }
}
