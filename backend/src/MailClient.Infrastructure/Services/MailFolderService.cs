using MailClient.Application.Interfaces;
using MailClient.Application.Network;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MailClient.Infrastructure.Services;

public sealed class MailFolderService(
    AppDbContext db,
    ICredentialProtector credentials,
    IMailFolderExplorer explorer,
    ILogger<MailFolderService> logger) : IMailFolderService
{
    public async Task<IReadOnlyList<MailFolderResponse>?> ListAsync(Guid userId, Guid accountId, CancellationToken cancellationToken)
    {
        var ownsAccount = await db.MailAccounts.AnyAsync(account => account.Id == accountId && account.UserId == userId, cancellationToken);
        if (!ownsAccount) return null;

        return await db.MailFolders
            .Where(folder => folder.MailAccountId == accountId)
            .OrderBy(folder => folder.Name)
            .Select(folder => new MailFolderResponse(
                folder.Id, folder.Name, folder.FullName, folder.FolderType, folder.UidValidity, folder.IsSyncEnabled))
            .ToListAsync(cancellationToken);
    }

    public async Task<MailFolderResponse?> SetSyncEnabledAsync(
        Guid userId,
        Guid accountId,
        Guid folderId,
        bool isSyncEnabled,
        CancellationToken cancellationToken)
    {
        var folder = await db.MailFolders
            .SingleOrDefaultAsync(folder => folder.Id == folderId && folder.MailAccountId == accountId && folder.MailAccount!.UserId == userId, cancellationToken);
        if (folder is null) return null;

        folder.IsSyncEnabled = isSyncEnabled;
        await db.SaveChangesAsync(cancellationToken);
        return ToResponse(folder);
    }

    public async Task<MailFolderRefreshResponse?> RefreshAsync(Guid userId, Guid accountId, CancellationToken cancellationToken)
    {
        var account = await db.MailAccounts.SingleOrDefaultAsync(item => item.Id == accountId && item.UserId == userId, cancellationToken);
        if (account is null) return null;

        IReadOnlyList<DiscoveredMailFolder> discovered;
        try
        {
            var password = credentials.Unprotect(account.EncryptedPassword);
            discovered = await explorer.ExploreAsync(
                new MailServerEndpoint(account.ImapHost, account.ImapPort, account.ImapSecurity),
                account.Username, password, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MailConnectionException ex)
        {
            logger.LogWarning(ex,
                "Mail folder discovery failed ({Failure}) for account {AccountId} of user {UserId}.",
                ex.Failure, accountId, userId);
            return new MailFolderRefreshResponse(false, ex.Message, []);
        }

        try
        {
            var foldersResponse = await UpsertAsync(accountId, discovered, cancellationToken);
            logger.LogInformation(
                "Mail folder discovery stored {Count} folders for account {AccountId} of user {UserId}.",
                foldersResponse.Count, accountId, userId);
            return new MailFolderRefreshResponse(true, "Folder discovery succeeded.", foldersResponse);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            logger.LogError(ex,
                "Failed to store discovered folders for account {AccountId} of user {UserId}.",
                accountId, userId);
            return new MailFolderRefreshResponse(false, "Failed to store discovered folders.", []);
        }
    }

    public async Task<IReadOnlyList<MailFolderResponse>> UpsertAsync(
        Guid accountId,
        IEnumerable<DiscoveredMailFolder> discovered,
        CancellationToken cancellationToken)
    {
        foreach (var item in discovered)
        {
            var existing = await db.MailFolders.SingleOrDefaultAsync(
                folder => folder.MailAccountId == accountId && folder.FullName == item.FullName, cancellationToken);
            if (existing is null)
            {
                db.MailFolders.Add(new MailClient.Domain.Entities.MailFolder
                {
                    Id = Guid.NewGuid(),
                    MailAccountId = accountId,
                    Name = item.Name,
                    FullName = item.FullName,
                    FolderType = item.FolderType,
                    UidValidity = item.UidValidity,
                    IsSyncEnabled = item.IsSyncEnabled
                });
            }
            else
            {
                existing.Name = item.Name;
                existing.FolderType = item.FolderType;
                existing.UidValidity = item.UidValidity;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return await db.MailFolders
            .Where(folder => folder.MailAccountId == accountId)
            .OrderBy(folder => folder.Name)
            .Select(folder => new MailFolderResponse(
                folder.Id, folder.Name, folder.FullName, folder.FolderType, folder.UidValidity, folder.IsSyncEnabled))
            .ToListAsync(cancellationToken);
    }

    private static MailFolderResponse ToResponse(MailClient.Domain.Entities.MailFolder folder) => new(
        folder.Id, folder.Name, folder.FullName, folder.FolderType, folder.UidValidity, folder.IsSyncEnabled);
}
