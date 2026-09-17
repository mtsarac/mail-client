using System.Net.Mail;
using System.Security.Cryptography;
using System.Text.Json;
using MailClient.Application.Accounts;
using MailClient.Application.Discovery;
using MailClient.Application.Mail;
using MailClient.Application.Sync;
using MailClient.Domain;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Authentication;
using MailClient.Infrastructure.Mail;
using MailClient.Infrastructure.OAuth;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MailClient.Infrastructure.Accounts;

public sealed class OAuthConnectionService(
    AppDbContext db,
    IEnumerable<IOAuthProvider> oauthProviders,
    OAuthStateProtector states,
    ICredentialProtector protector,
    IMailConnectionValidator connections,
    MailSessionService sessions,
    IJwtTokenIssuer jwt,
    InitialSyncQueue syncQueue,
    MailKitFolderExplorer explorer,
    ILogger<OAuthConnectionService> logger)
{
    public OAuthStartResponse Start(MailProvider provider, OAuthStartRequest request)
    {
        var oauthProvider = oauthProviders.SingleOrDefault(p => p.Provider == provider && p.IsConfigured)
            ?? throw new InvalidOperationException("oauth_provider_not_configured");
        var email = NormalizeEmail(request.Email);
        var redirectUri = oauthProvider.PrimaryRedirectUri;
        var (verifier, challenge) = OAuthStateProtector.CreatePkce();
        var payload = new OAuthStatePayload(provider, email, redirectUri, verifier, request.DeviceIdentifier, Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
        var state = states.Protect(payload);
        var url = oauthProvider.CreateAuthorizationUrl(email, redirectUri, state, challenge);
        return new OAuthStartResponse(url, state);
    }

    private static string NormalizeEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 320 || email.Any(char.IsControl))
            throw new InvalidOperationException("invalid_email");
        try
        {
            var parsed = new MailAddress(email.Trim());
            return string.Equals(parsed.Address, email.Trim(), StringComparison.OrdinalIgnoreCase)
                ? parsed.Address
                : throw new InvalidOperationException("invalid_email");
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("invalid_email");
        }
    }

    public async Task<TokenResponse> CompleteAsync(MailProvider provider, OAuthCompleteRequest request, CancellationToken cancellationToken)
    {
        OAuthStatePayload payload;
        try
        {
            payload = states.Consume(request.State);
        }
        catch (InvalidOperationException ex) when (ex.Message == "oauth_state_invalid")
        {
            throw;
        }
        if (payload.Provider != provider)
            throw new InvalidOperationException("oauth_state_invalid");
        var oauthProvider = oauthProviders.SingleOrDefault(p => p.Provider == provider && p.IsConfigured)
            ?? throw new InvalidOperationException("oauth_provider_not_configured");
        OAuthToken token;
        try
        {
            token = await oauthProvider.ExchangeCodeAsync(request.Code, payload.CodeVerifier, payload.RedirectUri, cancellationToken);
        }
        catch (InvalidOperationException ex) when (ex.Message == "oauth_code_exchange_failed" || ex.Message == "mail_account_needs_reauthentication")
        {
            throw;
        }
        var candidate = provider switch
        {
            MailProvider.Google => new MailServerCandidate(MailProvider.Google,
                new MailEndpoint("imap.gmail.com", 993, MailSecurity.SslOnConnect),
                new MailEndpoint("smtp.gmail.com", 465, MailSecurity.SslOnConnect),
                [AuthenticationMethod.OAuth2], DiscoverySource.KnownProvider),
            MailProvider.Microsoft => new MailServerCandidate(MailProvider.Microsoft,
                new MailEndpoint("outlook.office365.com", 993, MailSecurity.SslOnConnect),
                new MailEndpoint("smtp.office365.com", 587, MailSecurity.StartTls),
                [AuthenticationMethod.OAuth2], DiscoverySource.KnownProvider),
            _ => throw new InvalidOperationException("oauth_provider_not_configured")
        };
        try
        {
            await connections.ValidateOAuthCredentialsAsync(candidate, payload.Email, token.AccessToken, cancellationToken);
        }
        catch (MailConnectionException)
        {
            throw new InvalidOperationException("mail_authentication_failed");
        }
        var normalized = payload.Email.Trim().ToUpperInvariant();
        var account = await db.MailAccounts.Include(x => x.Credentials).SingleOrDefaultAsync(x => x.NormalizedEmailAddress == normalized, cancellationToken);
        var now = DateTime.UtcNow;
        if (account is null)
        {
            account = new MailAccount { Id = Guid.NewGuid(), EmailAddress = payload.Email.Trim(), NormalizedEmailAddress = normalized, CreatedAt = now };
            db.MailAccounts.Add(account);
        }
        account.Username = payload.Email;
        account.Provider = provider;
        account.AuthenticationMethod = AuthenticationMethod.OAuth2;
        account.ImapHost = candidate.Imap.Host;
        account.ImapPort = candidate.Imap.Port;
        account.ImapSecurity = candidate.Imap.Security;
        account.SmtpHost = candidate.Smtp.Host;
        account.SmtpPort = candidate.Smtp.Port;
        account.SmtpSecurity = candidate.Smtp.Security;
        account.DiscoverySource = candidate.Source;
        account.Status = MailAccountStatus.Active;
        account.UpdatedAt = now;
        account.LastAuthenticatedAt = now;
        var credential = account.Credentials.SingleOrDefault(x => x.AuthenticationMethod == AuthenticationMethod.OAuth2);
        if (credential is null)
        {
            credential = new MailCredential { Id = Guid.NewGuid(), MailAccount = account, AuthenticationMethod = AuthenticationMethod.OAuth2, Provider = provider, CreatedAt = now };
            account.Credentials.Add(credential);
        }
        credential.EncryptedMaterial = protector.Protect(JsonSerializer.Serialize(new OAuthCredentialMaterial(token.AccessToken, token.RefreshToken)));
        credential.ExpiresAt = token.ExpiresAt;
        credential.Scopes = token.Scopes;
        credential.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await RefreshFoldersBestEffortAsync(account, payload.Email, token.AccessToken, cancellationToken);
        var session = await sessions.CreateAsync(account.Id, payload.DeviceIdentifier, cancellationToken);
        await syncQueue.EnqueueAsync(SyncRequest.Account(account.Id), cancellationToken);
        var access = jwt.Issue(account.Id);
        return new TokenResponse(access.Token, session.Token, account.Id, access.ExpiresAt);
    }

    private async Task RefreshFoldersBestEffortAsync(MailAccount account, string username, string accessToken, CancellationToken cancellationToken)
    {
        try
        {
            var endpoint = new MailServerEndpoint(account.ImapHost, account.ImapPort, account.ImapSecurity);
            var discovered = await explorer.ExploreAsync(endpoint, username, accessToken, cancellationToken, AuthenticationMethod.OAuth2);
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
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Folder discovery failed for OAuth account {AccountId}; folders remain refreshable.", account.Id);
        }
    }
}
