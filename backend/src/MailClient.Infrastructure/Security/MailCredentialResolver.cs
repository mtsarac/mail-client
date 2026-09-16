using System.Collections.Concurrent;
using System.Text.Json;
using MailClient.Application.Accounts;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.OAuth;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Infrastructure.Security;

public sealed record OAuthCredentialMaterial(string AccessToken, string? RefreshToken);
public sealed record ResolvedCredential(MailAccount Account, string Username, string Secret, AuthenticationMethod AuthenticationMethod)
{
    public string Password => Secret;
}

public sealed class MailCredentialResolver(
    AppDbContext db,
    ICredentialProtector protector,
    IEnumerable<IOAuthProvider> oauthProviders)
{
    public MailCredentialResolver(AppDbContext db, ICredentialProtector protector)
        : this(db, protector, [])
    {
    }

    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> RefreshLocks = new();
    private static readonly TimeSpan RefreshWindow = TimeSpan.FromMinutes(5);

    public async Task<ResolvedCredential> ResolveAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var account = await LoadAccountAsync(accountId, cancellationToken);
        var credential = account.Credentials.SingleOrDefault(x => x.AuthenticationMethod == account.AuthenticationMethod)
            ?? throw new InvalidOperationException("credential_missing");
        if (account.AuthenticationMethod != AuthenticationMethod.OAuth2)
            return new ResolvedCredential(account, account.Username, protector.Unprotect(credential.EncryptedMaterial), account.AuthenticationMethod);
        return await ResolveOAuthAsync(accountId, cancellationToken);
    }

    private async Task<ResolvedCredential> ResolveOAuthAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var refreshLock = RefreshLocks.GetOrAdd(accountId, static _ => new SemaphoreSlim(1, 1));
        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            var account = await LoadAccountAsync(accountId, cancellationToken);
            var credential = account.Credentials.Single(x => x.AuthenticationMethod == AuthenticationMethod.OAuth2);
            var material = Deserialize(credential.EncryptedMaterial);
            if (credential.ExpiresAt is { } expiresAt && expiresAt > DateTime.UtcNow.Add(RefreshWindow))
                return new ResolvedCredential(account, account.Username, material.AccessToken, AuthenticationMethod.OAuth2);
            if (string.IsNullOrWhiteSpace(material.RefreshToken))
                return await RequireReauthenticationAsync(account, cancellationToken);
            var provider = oauthProviders.SingleOrDefault(item => item.Provider == account.Provider && item.IsConfigured)
                ?? throw new InvalidOperationException("oauth_provider_not_configured");
            try
            {
                var refreshed = await provider.RefreshAsync(material.RefreshToken, cancellationToken);
                var rotatedRefreshToken = refreshed.RefreshToken ?? material.RefreshToken;
                credential.EncryptedMaterial = protector.Protect(JsonSerializer.Serialize(new OAuthCredentialMaterial(refreshed.AccessToken, rotatedRefreshToken)));
                credential.ExpiresAt = refreshed.ExpiresAt;
                credential.Scopes = refreshed.Scopes;
                credential.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                return new ResolvedCredential(account, account.Username, refreshed.AccessToken, AuthenticationMethod.OAuth2);
            }
            catch (InvalidOperationException ex) when (ex.Message == "mail_account_needs_reauthentication")
            {
                return await RequireReauthenticationAsync(account, cancellationToken);
            }
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private async Task<MailAccount> LoadAccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var account = await db.MailAccounts.Include(x => x.Credentials)
            .SingleOrDefaultAsync(x => x.Id == accountId, cancellationToken)
            ?? throw new InvalidOperationException("mail_account_not_found");
        if (account.Status == MailAccountStatus.Disabled)
            throw new InvalidOperationException("mail_account_disabled");
        if (account.Status == MailAccountStatus.NeedsReauthentication)
            throw new InvalidOperationException("mail_account_needs_reauthentication");
        return account;
    }

    private OAuthCredentialMaterial Deserialize(string encryptedMaterial)
    {
        try
        {
            return JsonSerializer.Deserialize<OAuthCredentialMaterial>(protector.Unprotect(encryptedMaterial))
                ?? throw new InvalidOperationException("credential_missing");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("credential_missing");
        }
    }

    private async Task<ResolvedCredential> RequireReauthenticationAsync(MailAccount account, CancellationToken cancellationToken)
    {
        account.Status = MailAccountStatus.NeedsReauthentication;
        account.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        throw new InvalidOperationException("mail_account_needs_reauthentication");
    }
}
