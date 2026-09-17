using MailClient.Application.Accounts;
using MailClient.Application.Discovery;
using MailClient.Application.Mail;
using MailClient.Application.Runtime;
using MailClient.Application.Sync;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Authentication;
using MailClient.Infrastructure.Discovery;
using MailClient.Infrastructure.Mail;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MailClient.Infrastructure.Accounts;

public sealed class AccountConnectionService(
    AppDbContext db,
    IMailConnectionValidator connections,
    ICredentialProtector protector,
    MailSessionService sessions,
    IJwtTokenIssuer jwt,
    ISyncScheduler scheduler,
    MailKitFolderExplorer explorer,
    MailCredentialResolver credentialResolver,
    IRuntimePolicyProvider runtimePolicy,
    ILogger<AccountConnectionService> logger)
{
    public async Task<TokenResponse> ConnectAsync(DiscoveryState state, AuthenticationInput authentication, string? deviceIdentifier, CancellationToken cancellationToken) =>
        await ConnectCoreAsync(state.Email, state.Email, null, state.Candidate, authentication, deviceIdentifier, cancellationToken);

    public async Task<TokenResponse> ConnectManualAsync(ManualConnectRequest request, CancellationToken cancellationToken)
    {
        var candidate = new MailServerCandidate(MailProvider.Custom, new(request.Imap.Host, request.Imap.Port, request.Imap.Security), new(request.Smtp.Host, request.Smtp.Port, request.Smtp.Security), [AuthenticationMethod.Password, AuthenticationMethod.AppSpecificPassword], DiscoverySource.Manual);
        return await ConnectCoreAsync(request.Email, request.Username, request.DisplayName, candidate, request.Authentication, request.DeviceIdentifier, cancellationToken);
    }

    private async Task<TokenResponse> ConnectCoreAsync(string email, string username, string? displayName, MailServerCandidate candidate, AuthenticationInput authentication, string? deviceIdentifier, CancellationToken cancellationToken)
    {
        if (authentication.Type is not (AuthenticationMethod.Password or AuthenticationMethod.AppSpecificPassword) || string.IsNullOrWhiteSpace(authentication.Password)) throw new InvalidOperationException("unsupported_authentication_method");
        (await runtimePolicy.GetAsync(cancellationToken)).EnsureNewAccountAllowed(candidate.Provider, authentication.Type);
        if (!email.Contains('@', StringComparison.Ordinal)) throw new InvalidOperationException("invalid_email");
        var normalizedEmail = email.Trim().ToUpperInvariant();
        // Anonymous connection creates accounts only. Re-pointing an existing mailbox at different servers or
        // credentials would let any caller claim someone else's account with a mail server they control, so it
        // requires an authenticated account-scoped reconnect instead.
        if (await db.MailAccounts.AnyAsync(item => item.NormalizedEmailAddress == normalizedEmail, cancellationToken))
            throw new InvalidOperationException("mail_account_already_exists");
        var local = email[..email.IndexOf('@')];
        var usernames = new[] { username, email, local }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase);
        string? working = null;
        foreach (var candidateUsername in usernames)
        {
            try
            {
                await connections.ValidateCredentialsAsync(candidate, candidateUsername, authentication.Password, cancellationToken);
                working = candidateUsername;
                break;
            }
            catch (MailConnectionException ex) when (ex.Failure == MailConnectionFailure.Authentication && candidateUsername != usernames.Last())
            {
                continue;
            }
        }
        if (working is null) throw new InvalidOperationException("mail_authentication_failed");
        var now = DateTime.UtcNow;
        var account = new MailAccount { Id = Guid.NewGuid(), EmailAddress = email.Trim(), NormalizedEmailAddress = normalizedEmail, CreatedAt = now };
        db.MailAccounts.Add(account);
        account.DisplayName = displayName?.Trim() ?? account.DisplayName;
        account.Username = working;
        account.Provider = candidate.Provider;
        account.AuthenticationMethod = authentication.Type;
        account.ImapHost = candidate.Imap.Host; account.ImapPort = candidate.Imap.Port; account.ImapSecurity = candidate.Imap.Security;
        account.SmtpHost = candidate.Smtp.Host; account.SmtpPort = candidate.Smtp.Port; account.SmtpSecurity = candidate.Smtp.Security;
        account.DiscoverySource = candidate.Source; account.Status = MailAccountStatus.Active; account.UpdatedAt = now; account.LastAuthenticatedAt = now;
        var credential = account.Credentials.SingleOrDefault(x => x.AuthenticationMethod == authentication.Type);
        if (credential is null) { credential = new MailCredential { Id = Guid.NewGuid(), MailAccount = account, AuthenticationMethod = authentication.Type, Provider = candidate.Provider, CreatedAt = now }; account.Credentials.Add(credential); }
        credential.EncryptedMaterial = protector.Protect(authentication.Password); credential.UpdatedAt = now;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Unique index on NormalizedEmailAddress: a concurrent connection created the account first.
            throw new InvalidOperationException("mail_account_already_exists");
        }

        await RefreshFoldersBestEffortAsync(account, cancellationToken);
        var session = await sessions.CreateAsync(account.Id, deviceIdentifier, cancellationToken);
        await scheduler.ScheduleAccountAsync(account.Id, SyncOrigin.Initial, cancellationToken);
        var access = jwt.Issue(account.Id);
        return new(access.Token, session.Token, account.Id, access.ExpiresAt);
    }

    /// <summary>
    /// Authenticated credential/server update for the caller's own account. The account is located by the
    /// authenticated id, never by a submitted email address.
    /// </summary>
    public async Task<MailAccount> ReconnectAsync(Guid accountId, AccountReconnectRequest request, CancellationToken cancellationToken)
    {
        if (request.Authentication.Type is not (AuthenticationMethod.Password or AuthenticationMethod.AppSpecificPassword)
            || string.IsNullOrWhiteSpace(request.Authentication.Password))
            throw new InvalidOperationException("unsupported_authentication_method");
        var account = await db.MailAccounts.Include(item => item.Credentials)
            .SingleOrDefaultAsync(item => item.Id == accountId, cancellationToken)
            ?? throw new InvalidOperationException("mail_account_not_found");
        var policy = await runtimePolicy.GetAsync(cancellationToken);
        policy.EnsureExistingAccountAllowed(account.Provider);
        policy.EnsureAuthenticationMethodAllowed(account.Provider, request.Authentication.Type);

        var imap = request.Imap is { } imapInput
            ? new MailEndpoint(imapInput.Host, imapInput.Port, imapInput.Security)
            : new MailEndpoint(account.ImapHost, account.ImapPort, account.ImapSecurity);
        var smtp = request.Smtp is { } smtpInput
            ? new MailEndpoint(smtpInput.Host, smtpInput.Port, smtpInput.Security)
            : new MailEndpoint(account.SmtpHost, account.SmtpPort, account.SmtpSecurity);
        var candidate = new MailServerCandidate(
            account.Provider, imap, smtp,
            [AuthenticationMethod.Password, AuthenticationMethod.AppSpecificPassword],
            request.Imap is null && request.Smtp is null ? account.DiscoverySource : DiscoverySource.Manual);
        await connections.ValidateCredentialsAsync(candidate, account.Username, request.Authentication.Password, cancellationToken);

        var now = DateTime.UtcNow;
        account.AuthenticationMethod = request.Authentication.Type;
        account.ImapHost = imap.Host; account.ImapPort = imap.Port; account.ImapSecurity = imap.Security;
        account.SmtpHost = smtp.Host; account.SmtpPort = smtp.Port; account.SmtpSecurity = smtp.Security;
        account.DiscoverySource = candidate.Source;
        account.Status = MailAccountStatus.Active;
        account.UpdatedAt = now;
        account.LastAuthenticatedAt = now;
        var credential = account.Credentials.SingleOrDefault(item => item.AuthenticationMethod == request.Authentication.Type);
        if (credential is null)
        {
            credential = new MailCredential { Id = Guid.NewGuid(), MailAccount = account, AuthenticationMethod = request.Authentication.Type, Provider = account.Provider, CreatedAt = now };
            account.Credentials.Add(credential);
        }

        credential.EncryptedMaterial = protector.Protect(request.Authentication.Password);
        credential.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await RefreshFoldersBestEffortAsync(account, cancellationToken);
        await scheduler.ScheduleAccountAsync(account.Id, SyncOrigin.UserRequested, cancellationToken);
        return account;
    }

    public async Task<int> RefreshFoldersAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var resolved = await credentialResolver.ResolveAsync(accountId, cancellationToken);
        return await ReconcileFoldersAsync(resolved.Account, resolved.Username, resolved.Secret, resolved.AuthenticationMethod, cancellationToken);
    }

    /// <summary>
    /// Folder discovery after connect/reconnect authenticates with the credential persisted for the account,
    /// never with the values submitted on the request. The authentication method that reaches the mail server is
    /// therefore always the stored one, so a request can never select the mechanism a connection authenticates with.
    /// </summary>
    private async Task RefreshFoldersBestEffortAsync(MailAccount account, CancellationToken cancellationToken)
    {
        try
        {
            await RefreshFoldersAsync(account.Id, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Folder discovery failed for account {AccountId}; folders remain refreshable.", account.Id);
        }
    }

    private async Task<int> ReconcileFoldersAsync(MailAccount account, string username, string password, AuthenticationMethod authenticationMethod, CancellationToken cancellationToken)
    {
        var endpoint = new MailServerEndpoint(account.ImapHost, account.ImapPort, account.ImapSecurity);
        var discovered = await explorer.ExploreAsync(endpoint, username, password, cancellationToken, authenticationMethod);
        var existing = await db.MailFolders.Where(folder => folder.MailAccountId == account.Id).ToListAsync(cancellationToken);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in discovered)
        {
            seen.Add(folder.FullName);
            var row = existing.SingleOrDefault(item => string.Equals(item.FullName, folder.FullName, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                db.MailFolders.Add(new MailFolder
                {
                    Id = Guid.NewGuid(),
                    MailAccountId = account.Id,
                    Name = folder.Name,
                    FullName = folder.FullName,
                    FolderType = folder.FolderType,
                    UidValidity = folder.UidValidity,
                    IsSyncEnabled = folder.IsSyncEnabled,
                    IsAvailable = true
                });
            }
            else
            {
                row.Name = folder.Name;
                row.FolderType = folder.FolderType;
                row.UidValidity = folder.UidValidity;
                row.IsAvailable = true;
            }
        }

        foreach (var row in existing.Where(item => !seen.Contains(item.FullName)))
            row.IsAvailable = false;
        await db.SaveChangesAsync(cancellationToken);
        return discovered.Count;
    }
}
