using MailClient.Application.Mail;
using MailClient.Domain;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Mail;
using MailClient.Infrastructure.Observability;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Infrastructure.Services;

public sealed record FolderMutationResult(MailFolder? Folder, string? Error)
{
    public static FolderMutationResult Failed(string error) => new(null, error);
}

public static class MailFolderHierarchy
{
    public static string? ParentFullName(string fullName, string? delimiter)
    {
        if (string.IsNullOrEmpty(delimiter))
            return null;
        var index = fullName.LastIndexOf(delimiter, StringComparison.Ordinal);
        return index > 0 ? fullName[..index] : null;
    }

    public static bool IsDescendant(MailFolder candidate, MailFolder ancestor) =>
        !string.IsNullOrEmpty(ancestor.Delimiter)
        && candidate.Id != ancestor.Id
        && candidate.FullName.StartsWith(ancestor.FullName + ancestor.Delimiter, StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<Guid, Guid?> ParentIds(IReadOnlyCollection<MailFolder> folders)
    {
        var byName = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders.Where(folder => folder.IsAvailable))
            byName.TryAdd(folder.FullName, folder.Id);
        var byId = folders.Where(folder => folder.IsAvailable).Select(folder => folder.Id).ToHashSet();
        return folders.ToDictionary(
            folder => folder.Id,
            folder => folder.HasLocalParentOverride
                ? folder.LocalParentId is { } localId && byId.Contains(localId) ? localId : (Guid?)null
                : ParentFullName(folder.FullName, folder.Delimiter) is { } parent && byName.TryGetValue(parent, out var id)
                    ? id
                    : (Guid?)null);
    }
}

public sealed class FolderManagementService(
    AppDbContext db,
    IRemoteFolderManager remote,
    IFileStorage storage,
    AuditLogger audit)
{
    public const int MaxNameLength = 200;

    public async Task<FolderMutationResult> CreateAsync(Guid accountId, Guid? parentId, string? name, string correlationId, CancellationToken cancellationToken)
    {
        var folders = await db.MailFolders.Where(folder => folder.MailAccountId == accountId).ToListAsync(cancellationToken);
        MailFolder? parent = null;
        if (parentId is { } id)
        {
            parent = folders.SingleOrDefault(folder => folder.Id == id && folder.IsAvailable);
            if (parent is null)
                return FolderMutationResult.Failed("mail_folder_not_found");
        }

        var delimiter = parent?.Delimiter ?? folders.Select(folder => folder.Delimiter).FirstOrDefault(value => !string.IsNullOrEmpty(value));
        if (NormalizeName(name, delimiter) is not { } normalized)
            return FolderMutationResult.Failed("invalid_folder_name");

        var account = await db.MailAccounts.SingleAsync(item => item.Id == accountId, cancellationToken);
        var result = await remote.CreateAsync(account, parent?.FullName, normalized, cancellationToken);
        var localFallback = parent is not null && result.Outcome == RemoteFolderOutcome.Rejected;
        if (localFallback)
            result = await remote.CreateAsync(account, null, normalized, cancellationToken);
        if (Failure(result) is { } failure)
            return FolderMutationResult.Failed(failure);

        var created = result.Folder!;
        var row = folders.SingleOrDefault(folder => string.Equals(folder.FullName, created.FullName, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            row = new MailFolder
            {
                Id = Guid.NewGuid(),
                MailAccountId = accountId,
                FullName = created.FullName,
                IsSyncEnabled = MailFolder.SyncedByDefault(account.FolderSyncScope, created.FolderType)
            };
            db.MailFolders.Add(row);
            folders.Add(row);
        }

        row.Name = created.Name;
        row.FullName = created.FullName;
        row.DetectedFolderType = created.FolderType;
        row.UidValidity = created.UidValidity;
        row.Delimiter = created.Delimiter;
        row.IsAvailable = true;
        row.HasLocalParentOverride = localFallback;
        row.LocalParentId = localFallback ? parentId : null;
        MailFolder.ApplyRoles(folders);
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(accountId, AuditActions.MailFolderCreated, "MailFolder", row.Id.ToString(), null, correlationId, cancellationToken);
        return new(row, null);
    }

    public async Task<FolderMutationResult> RenameAsync(Guid accountId, Guid folderId, string? name, string correlationId, CancellationToken cancellationToken)
    {
        var folders = await db.MailFolders.Where(folder => folder.MailAccountId == accountId).ToListAsync(cancellationToken);
        var folder = folders.SingleOrDefault(item => item.Id == folderId && item.IsAvailable);
        if (folder is null)
            return FolderMutationResult.Failed("mail_folder_not_found");
        if (folder.FolderType != MailFolderType.Custom)
            return FolderMutationResult.Failed("mail_folder_protected");
        if (NormalizeName(name, folder.Delimiter) is not { } normalized)
            return FolderMutationResult.Failed("invalid_folder_name");

        var account = await db.MailAccounts.SingleAsync(item => item.Id == accountId, cancellationToken);
        var result = await remote.RenameAsync(account, folder.FullName, normalized, cancellationToken);
        if (Failure(result) is { } failure)
            return FolderMutationResult.Failed(failure);

        var renamed = result.Folder!;
        var oldFullName = folder.FullName;
        var descendants = folders.Where(item => MailFolderHierarchy.IsDescendant(item, folder)).ToList();
        var newNames = descendants
            .Select(item => (Folder: item, FullName: renamed.FullName + item.FullName[oldFullName.Length..]))
            .Append((Folder: folder, FullName: renamed.FullName))
            .ToList();
        var targets = newNames.Select(item => item.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var moving = newNames.Select(item => item.Folder.Id).ToHashSet();
        var stale = folders.Where(item => !moving.Contains(item.Id) && targets.Contains(item.FullName)).ToList();
        if (stale.Count > 0)
        {
            await RemoveLocalAsync(stale, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        foreach (var (item, fullName) in newNames)
            item.FullName = fullName;
        folder.Name = renamed.Name;
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(accountId, AuditActions.MailFolderRenamed, "MailFolder", folder.Id.ToString(), null, correlationId, cancellationToken);
        return new(folder, null);
    }

    public async Task<FolderMutationResult> MoveAsync(Guid accountId, Guid folderId, Guid? parentId, string correlationId, CancellationToken cancellationToken)
    {
        var folders = await db.MailFolders.Where(item => item.MailAccountId == accountId).ToListAsync(cancellationToken);
        var folder = folders.SingleOrDefault(item => item.Id == folderId && item.IsAvailable);
        if (folder is null)
            return FolderMutationResult.Failed("mail_folder_not_found");
        if (folder.FolderType != MailFolderType.Custom)
            return FolderMutationResult.Failed("mail_folder_protected");

        MailFolder? parent = null;
        if (parentId is { } id)
        {
            parent = folders.SingleOrDefault(item => item.Id == id && item.IsAvailable);
            if (parent is null)
                return FolderMutationResult.Failed("mail_folder_not_found");
        }

        var parents = MailFolderHierarchy.ParentIds(folders);
        // A local-only parent still owes the server a real RENAME, so a repeated
        // request must retry the remote move instead of short-circuiting.
        if (!folder.HasLocalParentOverride && parents[folder.Id] == parentId)
            return new(folder, null);
        for (var ancestor = parentId; ancestor is { } ancestorId; ancestor = parents[ancestorId])
        {
            if (ancestorId == folder.Id)
                return FolderMutationResult.Failed("mail_folder_cycle");
        }
        if (parent is not null && (parent.Id == folder.Id || MailFolderHierarchy.IsDescendant(parent, folder)))
            return FolderMutationResult.Failed("mail_folder_cycle");

        var actualParent = MailFolderHierarchy.ParentFullName(folder.FullName, folder.Delimiter);
        if (string.Equals(actualParent, parent?.FullName, StringComparison.OrdinalIgnoreCase))
        {
            folder.HasLocalParentOverride = false;
            folder.LocalParentId = null;
            await db.SaveChangesAsync(cancellationToken);
            return new(folder, null);
        }

        var descendants = folders.Where(item => MailFolderHierarchy.IsDescendant(item, folder)).ToList();
        var moving = descendants.Select(item => item.Id).Append(folder.Id).ToHashSet();
        var oldFullName = folder.FullName;
        var expectedFullName = parent is null ? folder.Name : parent.FullName + folder.Delimiter + folder.Name;
        if (folders.Any(item => !moving.Contains(item.Id) &&
            (string.Equals(item.FullName, expectedFullName, StringComparison.OrdinalIgnoreCase)
                || descendants.Any(child => string.Equals(item.FullName, expectedFullName + child.FullName[oldFullName.Length..], StringComparison.OrdinalIgnoreCase)))))
            return FolderMutationResult.Failed("mail_folder_exists");

        // If IMAP cannot represent the requested tree, retain the real remote path and
        // store only its presentation parent. Neither mail nor remote folders are migrated.
        var canRename = !string.IsNullOrEmpty(folder.Delimiter) && (parent is null || parent.Delimiter == folder.Delimiter);
        RemoteFolderResult? result = null;
        if (canRename)
        {
            var account = await db.MailAccounts.SingleAsync(item => item.Id == accountId, cancellationToken);
            result = await remote.MoveAsync(account, oldFullName, parent?.FullName, cancellationToken);
            if (result.Outcome != RemoteFolderOutcome.Rejected && Failure(result) is { } failure)
                return FolderMutationResult.Failed(failure);
        }
        if (result is null || result.Outcome == RemoteFolderOutcome.Rejected)
        {
            folder.HasLocalParentOverride = true;
            folder.LocalParentId = parentId;
            await db.SaveChangesAsync(cancellationToken);
            await audit.WriteAsync(accountId, AuditActions.MailFolderRenamed, "MailFolder", folder.Id.ToString(), null, correlationId, cancellationToken);
            return new(folder, null);
        }

        var newFullName = result.Folder!.FullName;
        // Never evict an existing cached folder or its messages when reconciling a remote rename.
        if (folders.Any(item => !moving.Contains(item.Id) &&
            (string.Equals(item.FullName, newFullName, StringComparison.OrdinalIgnoreCase)
                || descendants.Any(child => string.Equals(item.FullName, newFullName + child.FullName[oldFullName.Length..], StringComparison.OrdinalIgnoreCase)))))
            return FolderMutationResult.Failed("mail_folder_exists");

        foreach (var child in descendants)
            child.FullName = newFullName + child.FullName[oldFullName.Length..];
        folder.FullName = newFullName;
        folder.Name = result.Folder.Name;
        folder.HasLocalParentOverride = false;
        folder.LocalParentId = null;
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(accountId, AuditActions.MailFolderRenamed, "MailFolder", folder.Id.ToString(), null, correlationId, cancellationToken);
        return new(folder, null);
    }

    public async Task<string?> DeleteAsync(Guid accountId, Guid folderId, string correlationId, CancellationToken cancellationToken)
    {
        var folders = await db.MailFolders.Where(folder => folder.MailAccountId == accountId).ToListAsync(cancellationToken);
        var folder = folders.SingleOrDefault(item => item.Id == folderId && item.IsAvailable);
        if (folder is null)
            return "mail_folder_not_found";
        if (folder.FolderType != MailFolderType.Custom)
            return "mail_folder_protected";
        var parents = MailFolderHierarchy.ParentIds(folders);
        if (folders.Any(item => item.IsAvailable && parents[item.Id] == folder.Id))
            return "mail_folder_has_children";

        var account = await db.MailAccounts.SingleAsync(item => item.Id == accountId, cancellationToken);
        var result = await remote.DeleteAsync(account, folder.FullName, cancellationToken);
        if (result.Outcome != RemoteFolderOutcome.NotFound && Failure(result) is { } failure)
            return failure;

        await RemoveLocalAsync([folder], cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(accountId, AuditActions.MailFolderDeleted, "MailFolder", folder.Id.ToString(), null, correlationId, cancellationToken);
        return null;
    }

    public static string? NormalizeName(string? name, string? delimiter)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0 || trimmed.Length > MaxNameLength || trimmed.Any(char.IsControl))
            return null;
        if (!string.IsNullOrEmpty(delimiter) && trimmed.Contains(delimiter, StringComparison.Ordinal))
            return null;
        return trimmed;
    }

    private static string? Failure(RemoteFolderResult result) => result.Outcome switch
    {
        RemoteFolderOutcome.Succeeded => null,
        RemoteFolderOutcome.AlreadyExists => "mail_folder_exists",
        RemoteFolderOutcome.NotFound => "mail_folder_not_found",
        RemoteFolderOutcome.HasChildren => "mail_folder_has_children",
        RemoteFolderOutcome.NotEmpty => "mail_folder_not_empty",
        RemoteFolderOutcome.InvalidName => "invalid_folder_name",
        _ => "mail_folder_rejected"
    };

    private async Task RemoveLocalAsync(IReadOnlyCollection<MailFolder> folders, CancellationToken cancellationToken)
    {
        var ids = folders.Select(folder => folder.Id).ToList();
        var mails = await db.Mails.Where(mail => ids.Contains(mail.MailFolderId)).ToListAsync(cancellationToken);
        var mailIds = mails.Select(mail => mail.Id).ToList();
        var attachments = await db.Attachments.Where(attachment => mailIds.Contains(attachment.MailId)).ToListAsync(cancellationToken);
        foreach (var attachment in attachments)
            await storage.DeleteAsync(attachment.StoragePath, cancellationToken);
        db.Attachments.RemoveRange(attachments);
        db.Mails.RemoveRange(mails);
        db.SyncStates.RemoveRange(await db.SyncStates.Where(state => ids.Contains(state.MailFolderId)).ToListAsync(cancellationToken));
        db.MailFolders.RemoveRange(folders);
    }
}
