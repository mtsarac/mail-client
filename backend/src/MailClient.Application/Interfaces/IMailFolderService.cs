using MailClient.Domain.Enums;

namespace MailClient.Application.Interfaces;

public interface IMailFolderService
{
    Task<IReadOnlyList<MailFolderResponse>?> ListAsync(Guid userId, Guid accountId, CancellationToken cancellationToken);
    Task<MailFolderResponse?> SetSyncEnabledAsync(Guid userId, Guid accountId, Guid folderId, bool isSyncEnabled, CancellationToken cancellationToken);
    Task<MailFolderRefreshResponse?> RefreshAsync(Guid userId, Guid accountId, CancellationToken cancellationToken);
}

public sealed record MailFolderResponse(
    Guid Id,
    string Name,
    string FullName,
    MailFolderType FolderType,
    uint UidValidity,
    bool IsSyncEnabled,
    bool IsAvailable);

public sealed record MailFolderRefreshResponse(
    bool Succeeded,
    string Message,
    IReadOnlyList<MailFolderResponse> Folders);
