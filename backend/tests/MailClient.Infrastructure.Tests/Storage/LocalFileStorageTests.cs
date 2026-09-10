using System.Text;
using MailClient.Infrastructure.Storage;

namespace MailClient.Infrastructure.Tests.Storage;

public sealed class LocalFileStorageTests
{
    [Fact]
    public async Task SaveAsync_WritesAnOpaqueRelativePath()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var accountId = Guid.NewGuid();
        var mailId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();

        try
        {
            var storage = new LocalFileStorage(root);
            await using var content = new MemoryStream(Encoding.UTF8.GetBytes("attachment"));

            var stored = await storage.SaveAsync(
                accountId,
                mailId,
                attachmentId,
                (destination, ct) => content.CopyToAsync(destination, ct),
                100,
                CancellationToken.None);
            var path = stored.RelativePath;

            Assert.False(Path.IsPathRooted(path));
            Assert.Equal(Path.Combine("attachments", accountId.ToString("N"), mailId.ToString("N"), attachmentId.ToString("N")), path);
            Assert.Equal("attachment", await File.ReadAllTextAsync(Path.Combine(root, path)));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task SaveAsync_OverLimitRemovesTemporaryFile()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var storage = new LocalFileStorage(root);
            var bytes = Encoding.UTF8.GetBytes("too large");

            await Assert.ThrowsAsync<AttachmentLimitExceededException>(() => storage.SaveAsync(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                (destination, ct) => destination.WriteAsync(bytes, ct).AsTask(),
                bytes.Length - 1,
                CancellationToken.None));

            Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }
}
