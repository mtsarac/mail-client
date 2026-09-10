using MailClient.Application.Interfaces;

namespace MailClient.Infrastructure.Storage;

public sealed class LocalFileStorage(string rootPath) : IFileStorage
{
    public async Task<string> SaveAsync(
        Guid accountId,
        Guid mailId,
        Guid attachmentId,
        Stream content,
        CancellationToken cancellationToken)
    {
        var relativePath = Path.Combine(
            "attachments",
            accountId.ToString("N"),
            mailId.ToString("N"),
            attachmentId.ToString("N"));
        var destination = Path.Combine(rootPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var file = File.Create(destination);
        await content.CopyToAsync(file, cancellationToken);
        return relativePath;
    }
}
