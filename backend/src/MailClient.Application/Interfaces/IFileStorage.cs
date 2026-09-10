namespace MailClient.Application.Interfaces;

public interface IFileStorage
{
    Task<string> SaveAsync(
        Guid accountId,
        Guid mailId,
        Guid attachmentId,
        Stream content,
        CancellationToken cancellationToken);
}
