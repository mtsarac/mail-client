using MailClient.Infrastructure.Storage;

namespace MailClient.Tests;

public sealed class LocalAttachmentStorageTests
{
    [Fact]
    public void Resolve_RejectsTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var storage = new LocalAttachmentStorage(root);
        Assert.Throws<InvalidOperationException>(() => storage.Resolve("../secret"));
    }
}
