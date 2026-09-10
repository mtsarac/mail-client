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

            var path = await storage.SaveAsync(accountId, mailId, attachmentId, content, CancellationToken.None);

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
}
