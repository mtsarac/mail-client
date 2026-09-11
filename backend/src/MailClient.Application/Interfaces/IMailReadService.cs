using MailClient.Application;

// Two-way read/unread seam: applies flag changes to IMAP before local state.
namespace MailClient.Application.Interfaces;

public interface IMailReadService
{
    Task<ServiceResult<MailReadDto>> SetReadAsync(
        Guid userId,
        Guid mailId,
        bool isRead,
        CancellationToken cancellationToken);
}

public sealed record MailReadDto(Guid MailId, bool IsRead);
