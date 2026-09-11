using MailClient.Application.Interfaces;
using MailClient.Application.Network;
using MailClient.Application.Validation;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MailClient.Infrastructure.Services;

public sealed class MailAccountService(
    AppDbContext db,
    ICredentialProtector credentials,
    IHostEnvironment environment,
    IMailConnectivityTester tester,
    IOutboundHostValidator hosts,
    IFileStorage storage,
    ILogger<MailAccountService> logger) : IMailAccountService
{
    public async Task<IReadOnlyList<MailAccountResponse>> ListAsync(Guid userId, CancellationToken cancellationToken)
    {
        return await db.MailAccounts
            .Where(account => account.UserId == userId)
            .OrderBy(account => account.EmailAddress)
            .Select(account => new MailAccountResponse(
                account.Id, account.EmailAddress, account.DisplayName, account.Username,
                account.ImapHost, account.ImapPort, account.ImapSecurity,
                account.SmtpHost, account.SmtpPort, account.SmtpSecurity,
                account.SaveSentCopy, account.IsActive))
            .ToListAsync(cancellationToken);
    }

    public async Task<MailAccountResponse?> GetAsync(Guid userId, Guid accountId, CancellationToken cancellationToken)
    {
        var account = await FindOwnedAsync(userId, accountId, cancellationToken);
        return account is null ? null : ToResponse(account);
    }

    public async Task<MailAccountResponse> CreateAsync(Guid userId, MailAccountRequest request, CancellationToken cancellationToken)
    {
        Validate(request, passwordRequired: true);
        var now = DateTime.UtcNow;
        var email = request.EmailAddress.Trim().ToLowerInvariant();
        if (await db.MailAccounts.AnyAsync(
                account => account.UserId == userId && account.EmailAddress == email, cancellationToken))
            throw new RequestConflictException("emailAddress", "A mail account with this email address already exists.");

        var account = new MailAccount
        {
            UserId = userId,
            EmailAddress = email,
            DisplayName = request.DisplayName.Trim(),
            Username = request.Username.Trim(),
            EncryptedPassword = credentials.Protect(request.Password),
            ImapHost = request.ImapHost.Trim(),
            ImapPort = request.ImapPort,
            ImapSecurity = request.ImapSecurity,
            SmtpHost = request.SmtpHost.Trim(),
            SmtpPort = request.SmtpPort,
            SmtpSecurity = request.SmtpSecurity,
            SaveSentCopy = request.SaveSentCopy,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.MailAccounts.Add(account);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (DbUniqueViolation.IsUniqueViolationFor(ex, "IX_MailAccounts"))
        {
            // Lost the check-then-insert race; the constraint is authoritative.
            throw new RequestConflictException("emailAddress", "A mail account with this email address already exists.");
        }

        logger.LogInformation("User {UserId} created mail account {AccountId}.", userId, account.Id);
        return ToResponse(account);
    }

    public async Task<MailAccountResponse?> UpdateAsync(Guid userId, Guid accountId, UpdateMailAccountRequest request, CancellationToken cancellationToken)
    {
        Validate(request);
        var account = await FindOwnedAsync(userId, accountId, cancellationToken);
        if (account is null) return null;

        var email = request.EmailAddress.Trim().ToLowerInvariant();
        if (!string.Equals(account.EmailAddress, email, StringComparison.Ordinal)
            && await db.MailAccounts.AnyAsync(
                other => other.UserId == userId && other.Id != accountId && other.EmailAddress == email, cancellationToken))
            throw new RequestConflictException("emailAddress", "A mail account with this email address already exists.");

        var username = request.Username.Trim();
        var imapHost = request.ImapHost.Trim();
        // IMAP identity-defining fields: Username, ImapHost, ImapPort,
        // ImapSecurity. EmailAddress alone does NOT reset the cache: it is
        // only per-user uniqueness/display/From metadata, never the IMAP
        // authentication identity. DisplayName/SMTP fields/SaveSentCopy and
        // password-only rotation never reset either.
        var imapIdentityChanged =
            !string.Equals(account.Username, username, StringComparison.Ordinal)
            || !string.Equals(account.ImapHost, imapHost, StringComparison.OrdinalIgnoreCase)
            || account.ImapPort != request.ImapPort
            || account.ImapSecurity != request.ImapSecurity;

        if (!imapIdentityChanged)
        {
            ApplyConfiguration(account, email, request, username, imapHost,
                request.Password is null ? null : credentials.Protect(request.Password));
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (DbUniqueViolation.IsUniqueViolationFor(ex, "IX_MailAccounts"))
            {
                throw new RequestConflictException("emailAddress", "A mail account with this email address already exists.");
            }

            logger.LogInformation("User {UserId} updated mail account {AccountId}.", userId, accountId);
            return ToResponse(account);
        }

        // Serialize against in-flight folder syncs the same way deletion
        // does: sync holds these advisory locks while committing, so no new
        // cached state can land during the reset. The account lock additionally
        // serializes against folder discovery commits, which re-check the
        // account fingerprint under the same lock before storing results.
        await using var accountLock = await AccountAdvisoryLock.AcquireAsync(db, accountId, cancellationToken);
        var folderIds = await db.MailFolders
            .Where(folder => folder.MailAccountId == accountId && folder.IsSyncEnabled)
            .Select(folder => folder.Id)
            .ToListAsync(cancellationToken);
        var heldLocks = new List<FolderAdvisoryLock>(folderIds.Count);
        List<string> obsoletePaths;
        try
        {
            foreach (var folderId in folderIds)
                heldLocks.Add(await FolderAdvisoryLock.AcquireAsync(db, folderId, cancellationToken));

            obsoletePaths = await db.Attachments
                .Where(attachment => db.Mails.Any(mail => mail.Id == attachment.MailId && mail.MailAccountId == accountId))
                .Select(attachment => attachment.StoragePath)
                .ToListAsync(cancellationToken);

            // Folder deletion cascades to mails, attachment metadata and sync
            // states; skipped UIDs are removed explicitly.
            db.SyncSkippedUids.RemoveRange(db.SyncSkippedUids.Where(skip =>
                db.MailFolders.Any(folder => folder.Id == skip.MailFolderId && folder.MailAccountId == accountId)));
            db.Attachments.RemoveRange(db.Attachments.Where(attachment =>
                db.Mails.Any(mail => mail.Id == attachment.MailId && mail.MailAccountId == accountId)));
            db.Mails.RemoveRange(db.Mails.Where(mail => mail.MailAccountId == accountId));
            db.SyncStates.RemoveRange(db.SyncStates.Where(state =>
                db.MailFolders.Any(folder => folder.Id == state.MailFolderId && folder.MailAccountId == accountId)));
            db.MailFolders.RemoveRange(db.MailFolders.Where(folder => folder.MailAccountId == accountId));

            ApplyConfiguration(account, email, request, username, imapHost,
                request.Password is null ? null : credentials.Protect(request.Password));
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (DbUniqueViolation.IsUniqueViolationFor(ex, "IX_MailAccounts"))
            {
                throw new RequestConflictException("emailAddress", "A mail account with this email address already exists.");
            }
        }
        finally
        {
            for (var index = heldLocks.Count - 1; index >= 0; index--)
                await heldLocks[index].DisposeAsync();
        }

        logger.LogInformation(
            "User {UserId} changed IMAP identity for mail account {AccountId}; cached mailbox state reset.", userId, accountId);

        // Storage cleanup runs after the DB commit so metadata rows never
        // reference deleted files; failures are logged, never restored.
        foreach (var path in obsoletePaths)
        {
            try
            {
                await storage.DeleteAsync(path, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Attachment cleanup failed after IMAP identity change for account {AccountId}.",
                    accountId);
            }
        }

        return ToResponse(account);
    }

    private static void ApplyConfiguration(
        MailAccount account, string email, UpdateMailAccountRequest request, string username, string imapHost, string? protectedPassword)
    {
        account.EmailAddress = email;
        account.DisplayName = request.DisplayName.Trim();
        account.Username = username;
        if (protectedPassword is not null)
            account.EncryptedPassword = protectedPassword;
        account.ImapHost = imapHost;
        account.ImapPort = request.ImapPort;
        account.ImapSecurity = request.ImapSecurity;
        account.SmtpHost = request.SmtpHost.Trim();
        account.SmtpPort = request.SmtpPort;
        account.SmtpSecurity = request.SmtpSecurity;
        account.SaveSentCopy = request.SaveSentCopy;
        account.UpdatedAt = DateTime.UtcNow;
    }

    public async Task<bool> DeleteAsync(Guid userId, Guid accountId, CancellationToken cancellationToken)
    {
        var account = await FindOwnedAsync(userId, accountId, cancellationToken);
        if (account is null) return false;

        // Deactivate first so the background poll stops picking up this account.
        account.IsActive = false;
        await db.SaveChangesAsync(cancellationToken);

        // Serialize against in-flight folder syncs: they hold the same advisory
        // locks while committing, so no new attachment can land during deletion.
        var folderIds = await db.MailFolders
            .Where(folder => folder.MailAccountId == accountId && folder.IsSyncEnabled)
            .Select(folder => folder.Id)
            .ToListAsync(cancellationToken);
        var heldLocks = new List<FolderAdvisoryLock>(folderIds.Count);
        try
        {
            foreach (var folderId in folderIds)
                heldLocks.Add(await FolderAdvisoryLock.AcquireAsync(db, folderId, cancellationToken));

            db.MailAccounts.Remove(account);
            await db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            for (var index = heldLocks.Count - 1; index >= 0; index--)
                await heldLocks[index].DisposeAsync();
        }
        logger.LogInformation("User {UserId} deleted mail account {AccountId}.", userId, accountId);

        // Storage cleanup after the DB commit; scoped to the account directory
        // and idempotent. Failure must not block deletion.
        try
        {
            await storage.DeleteAccountAsync(accountId, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Attachment cleanup failed after deleting account {AccountId}. Path retained for later sweep.",
                accountId);
        }

        return true;
    }

    public async Task<MailAccountTestResponse?> TestAsync(Guid userId, Guid accountId, CancellationToken cancellationToken)
    {
        var account = await FindOwnedAsync(userId, accountId, cancellationToken);
        if (account is null) return null;

        try
        {
            var password = credentials.Unprotect(account.EncryptedPassword);
            await tester.TestImapAsync(
                new MailServerEndpoint(account.ImapHost, account.ImapPort, account.ImapSecurity),
                account.Username, password, cancellationToken);
            await tester.TestSmtpAsync(
                new MailServerEndpoint(account.SmtpHost, account.SmtpPort, account.SmtpSecurity),
                account.Username, password, cancellationToken);
            logger.LogInformation("Mail connection test succeeded for account {AccountId} of user {UserId}.", accountId, userId);
            return new MailAccountTestResponse(true, "IMAP and SMTP connections succeeded.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MailConnectionException ex)
        {
            logger.LogWarning(ex,
                "Mail connection test failed ({Failure}) for account {AccountId} of user {UserId}.",
                ex.Failure, accountId, userId);
            return new MailAccountTestResponse(false, ex.Message);
        }
    }

    private async Task<MailAccount?> FindOwnedAsync(Guid userId, Guid accountId, CancellationToken cancellationToken) =>
        await db.MailAccounts.SingleOrDefaultAsync(account => account.Id == accountId && account.UserId == userId, cancellationToken);

    private void Validate(MailAccountRequest request, bool passwordRequired)
    {
        var errors = new Dictionary<string, string[]>();
        RequestValidator.RequireEmail(request?.EmailAddress, "emailAddress", errors);
        RequestValidator.RequireDisplayName(request?.DisplayName, "displayName", 250, errors);
        RequestValidator.RequireUsername(request?.Username, "username", errors);
        if (passwordRequired)
            RequestValidator.RequireMailboxPassword(request?.Password, "password", errors);
        RequestValidator.RequireHost(request?.ImapHost, "imapHost", errors);
        RequestValidator.RequirePort(request?.ImapPort ?? 0, "imapPort", errors);
        RequestValidator.RequireHost(request?.SmtpHost, "smtpHost", errors);
        RequestValidator.RequirePort(request?.SmtpPort ?? 0, "smtpPort", errors);
        if (request is not null)
        {
            RequestValidator.RequireDefinedEnum(request.ImapSecurity, "imapSecurity", errors);
            RequestValidator.RequireDefinedEnum(request.SmtpSecurity, "smtpSecurity", errors);
        }
        CheckLiteralHost(request?.ImapHost, "imapHost", errors);
        CheckLiteralHost(request?.SmtpHost, "smtpHost", errors);
        if (!environment.IsDevelopment() && !environment.IsEnvironment("Test") && request is not null
            && (request.ImapSecurity == MailSecurity.None || request.SmtpSecurity == MailSecurity.None))
            errors["security"] = ["MailSecurity.None is allowed only in Development or Test."];
        RequestValidator.ThrowIfInvalid(errors);
    }

    private void Validate(UpdateMailAccountRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        RequestValidator.RequireEmail(request?.EmailAddress, "emailAddress", errors);
        RequestValidator.RequireDisplayName(request?.DisplayName, "displayName", 250, errors);
        RequestValidator.RequireUsername(request?.Username, "username", errors);
        if (request?.Password is not null)
            RequestValidator.RequireMailboxPassword(request.Password, "password", errors);
        RequestValidator.RequireHost(request?.ImapHost, "imapHost", errors);
        RequestValidator.RequirePort(request?.ImapPort ?? 0, "imapPort", errors);
        RequestValidator.RequireHost(request?.SmtpHost, "smtpHost", errors);
        RequestValidator.RequirePort(request?.SmtpPort ?? 0, "smtpPort", errors);
        if (request is not null)
        {
            RequestValidator.RequireDefinedEnum(request.ImapSecurity, "imapSecurity", errors);
            RequestValidator.RequireDefinedEnum(request.SmtpSecurity, "smtpSecurity", errors);
        }
        CheckLiteralHost(request?.ImapHost, "imapHost", errors);
        CheckLiteralHost(request?.SmtpHost, "smtpHost", errors);
        if (!environment.IsDevelopment() && !environment.IsEnvironment("Test") && request is not null
            && (request.ImapSecurity == MailSecurity.None || request.SmtpSecurity == MailSecurity.None))
            errors["security"] = ["MailSecurity.None is allowed only in Development or Test."];
        RequestValidator.ThrowIfInvalid(errors);
    }

    private void CheckLiteralHost(string? host, string field, Dictionary<string, string[]> errors)
    {
        if (errors.ContainsKey(field))
            return;

        var check = hosts.CheckLiteralHost(host);
        if (!check.Allowed)
        {
            logger.LogDebug("Rejected {Field} value at validation: {Reason}.", field, check.Reason);
            errors[field] = ["Mail host is not allowed."];
        }
    }

    private static MailAccountResponse ToResponse(MailAccount account) => new(
        account.Id, account.EmailAddress, account.DisplayName, account.Username,
        account.ImapHost, account.ImapPort, account.ImapSecurity,
        account.SmtpHost, account.SmtpPort, account.SmtpSecurity,
        account.SaveSentCopy, account.IsActive);
}
