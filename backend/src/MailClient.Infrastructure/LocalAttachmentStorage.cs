using MailClient.Application.Mail;

namespace MailClient.Infrastructure.Storage;

public sealed class BoundedWriteStream(Stream inner, long maxBytes) : Stream
{
    private long _written;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _written;
    public override long Position { get => _written; set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count)
    {
        if (_written + count > maxBytes)
            throw new AttachmentLimitExceededException();
        inner.Write(buffer, offset, count);
        _written += count;
    }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_written + buffer.Length > maxBytes)
            throw new AttachmentLimitExceededException();
        await inner.WriteAsync(buffer, cancellationToken);
        _written += buffer.Length;
    }
}

public sealed class LocalAttachmentStorage(string rootPath) : IFileStorage
{
    public string RootPath => Path.GetFullPath(rootPath);

    public string Resolve(string relativePath)
    {
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!path.StartsWith(root, StringComparison.Ordinal)) throw new InvalidOperationException("Attachment path escapes storage root.");
        return path;
    }

    public async Task<StoredFile> SaveAsync(
        Guid accountId,
        Guid mailId,
        Guid attachmentId,
        Func<Stream, CancellationToken, Task> write,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var relativePath = AttachmentPath.Relative(accountId, mailId, attachmentId);
        var destination = Resolve(relativePath);
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
        return Task.FromResult<Stream>(new FileStream(Resolve(relativePath), FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true));
    }

    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(Resolve(relativePath));
        return Task.CompletedTask;
    }

    public Task DeleteAccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var accountDir = Resolve(AttachmentPath.AccountPrefix(accountId));
        if (Directory.Exists(accountDir))
            Directory.Delete(accountDir, recursive: true);
        return Task.CompletedTask;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(RootPath);
            var probe = Path.Combine(RootPath, $".health-{Guid.NewGuid():N}.tmp");
            await File.WriteAllBytesAsync(probe, [], cancellationToken);
            File.Delete(probe);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }
}
