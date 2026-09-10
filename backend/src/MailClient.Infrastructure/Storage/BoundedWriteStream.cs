namespace MailClient.Infrastructure.Storage;

public sealed class AttachmentLimitExceededException : Exception;

internal sealed class BoundedWriteStream(Stream inner, long maximum) : Stream
{
    private long written;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => written;
    public override long Position { get => written; set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) { Check(count); inner.Write(buffer, offset, count); }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) { Check(buffer.Length); await inner.WriteAsync(buffer, ct); }
    private void Check(int count) { if (written + count > maximum) throw new AttachmentLimitExceededException(); written += count; }
}
