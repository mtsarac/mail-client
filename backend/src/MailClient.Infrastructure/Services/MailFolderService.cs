using MailClient.Application.Interfaces;
using MailClient.Application.Network;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

// Folder listing, sync toggling, and discovery refresh for user accounts.
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
                folder.Id, folder.Name, folder.FullName, folder.FolderType, folder.UidValidity, folder.IsSyncEnabled, folder.IsAvailable))
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

        var discoveredAgainst = Fingerprint(account);
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
            await using var accountLock = await AccountAdvisoryLock.AcquireAsync(db, accountId, cancellationToken);
            var current = await db.MailAccounts
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == accountId && item.UserId == userId, cancellationToken);
            if (current is null)
                return new MailFolderRefreshResponse(false, "Mail account no longer exists.", []);
            if (!string.Equals(discoveredAgainst, Fingerprint(current), StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "Discarding folder discovery for account {AccountId}: the account was reconfigured during discovery.",
                    accountId);
                return new MailFolderRefreshResponse(false, "Mail account was reconfigured during folder discovery; refresh again.", []);
            }

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
        var discoveredByName = discovered
            .GroupBy(item => item.FullName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var existing = await db.MailFolders
            .Where(folder => folder.MailAccountId == accountId)
            .ToListAsync(cancellationToken);

        foreach (var folder in existing)
        {
            if (!discoveredByName.TryGetValue(folder.FullName, out var item))
            {
                folder.IsAvailable = false;
                continue;
            }

            folder.Name = item.Name;
            folder.FolderType = item.FolderType;
            folder.UidValidity = item.UidValidity;
            folder.IsAvailable = true;
        }

        var existingNames = existing
            .Select(folder => folder.FullName)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var item in discoveredByName.Values)
        {
            if (existingNames.Contains(item.FullName))
                continue;
            db.MailFolders.Add(new MailClient.Domain.Entities.MailFolder
            {
                Id = Guid.NewGuid(),
                MailAccountId = accountId,
                Name = item.Name,
                FullName = item.FullName,
                FolderType = item.FolderType,
                UidValidity = item.UidValidity,
                IsSyncEnabled = item.IsSyncEnabled,
                IsAvailable = true
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        return await db.MailFolders
            .Where(folder => folder.MailAccountId == accountId)
            .OrderBy(folder => folder.Name)
            .Select(folder => new MailFolderResponse(
                folder.Id, folder.Name, folder.FullName, folder.FolderType, folder.UidValidity, folder.IsSyncEnabled, folder.IsAvailable))
            .ToListAsync(cancellationToken);
    }

    private static MailFolderResponse ToResponse(MailClient.Domain.Entities.MailFolder folder) => new(
        folder.Id, folder.Name, folder.FullName, folder.FolderType, folder.UidValidity, folder.IsSyncEnabled, folder.IsAvailable);

    private static string Fingerprint(MailClient.Domain.Entities.MailAccount account) =>
        string.Join('\n',
            account.Username,
            account.ImapHost.ToLowerInvariant(),
            account.ImapPort.ToString(),
            ((int)account.ImapSecurity).ToString(),
            account.UpdatedAt.ToString("o"));
}
