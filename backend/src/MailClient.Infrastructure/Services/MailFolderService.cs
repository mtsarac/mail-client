using MailClient.Application.Interfaces;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Infrastructure.Services;

public sealed record DiscoveredMailFolder(string Name, string FullName, FolderAttributes Attributes, uint UidValidity);

public sealed class MailFolderService(AppDbContext db, ICredentialProtector credentials) : IMailFolderService
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

        try
        {
            var password = credentials.Unprotect(account.EncryptedPassword);
            using var imap = new ImapClient();
            await imap.ConnectAsync(account.ImapHost, account.ImapPort, ToSocketOptions(account.ImapSecurity), cancellationToken);
            await imap.AuthenticateAsync(account.Username, password, cancellationToken);

            var discovered = new List<DiscoveredMailFolder>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await CollectAsync(imap.Inbox, discovered, seen, cancellationToken);

            foreach (var ns in imap.PersonalNamespaces)
            {
                var folders = await imap.GetFoldersAsync(ns, false, cancellationToken);
                foreach (var folder in folders)
                    await CollectAsync(folder, discovered, seen, cancellationToken);
            }

            if (discovered.Count == 0)
            {
                foreach (var folder in await imap.Inbox.GetSubfoldersAsync(false, cancellationToken))
                    await CollectAsync(folder, discovered, seen, cancellationToken);
            }

            var foldersResponse = await UpsertAsync(accountId, discovered, cancellationToken);
            await imap.DisconnectAsync(true, cancellationToken);
            return new MailFolderRefreshResponse(true, "Folder discovery succeeded.", foldersResponse);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return new MailFolderRefreshResponse(false, "IMAP folder discovery failed.", []);
        }
    }

    public async Task<IReadOnlyList<MailFolderResponse>> UpsertAsync(
        Guid accountId,
        IEnumerable<DiscoveredMailFolder> discovered,
        CancellationToken cancellationToken)
    {
        foreach (var item in discovered)
        {
            var classification = MailFolderDiscovery.Classify(item.Attributes, item.FullName);
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
                    FolderType = classification.FolderType,
                    UidValidity = item.UidValidity,
                    IsSyncEnabled = classification.IsSyncEnabled
                });
            }
            else
            {
                existing.Name = item.Name;
                existing.FolderType = classification.FolderType;
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

    private static async Task CollectAsync(
        IMailFolder folder,
        List<DiscoveredMailFolder> discovered,
        HashSet<string> seen,
        CancellationToken cancellationToken)
    {
        if (seen.Add(folder.FullName))
        {
            uint uidValidity = 0;
            if (!folder.Attributes.HasFlag(FolderAttributes.NoSelect))
            {
                try
                {
                    await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
                    uidValidity = folder.UidValidity;
                    await folder.CloseAsync(false, cancellationToken);
                }
                catch (Exception)
                {
                    uidValidity = folder.UidValidity;
                }
            }

            discovered.Add(new DiscoveredMailFolder(folder.Name, folder.FullName, folder.Attributes, uidValidity));
        }

        IList<IMailFolder> children;
        try
        {
            children = await folder.GetSubfoldersAsync(false, cancellationToken);
        }
        catch (Exception)
        {
            return;
        }

        foreach (var child in children)
            await CollectAsync(child, discovered, seen, cancellationToken);
    }

    private static SecureSocketOptions ToSocketOptions(MailSecurity security) => security switch
    {
        MailSecurity.None => SecureSocketOptions.None,
        MailSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        MailSecurity.StartTls => SecureSocketOptions.StartTls,
        _ => throw new ArgumentOutOfRangeException(nameof(security))
    };

    private static MailFolderResponse ToResponse(MailClient.Domain.Entities.MailFolder folder) => new(
        folder.Id, folder.Name, folder.FullName, folder.FolderType, folder.UidValidity, folder.IsSyncEnabled);
}
