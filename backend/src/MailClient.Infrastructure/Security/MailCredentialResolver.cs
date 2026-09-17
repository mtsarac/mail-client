using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using MailClient.Application.Accounts;
using MailClient.Application.Mail;
using MailClient.Application.Observability;
using MailClient.Application.Runtime;
using MailClient.Application.Sync;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.OAuth;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MailClient.Infrastructure.Security;

public sealed record OAuthCredentialMaterial(string AccessToken, string? RefreshToken);
public sealed record ResolvedCredential(MailAccount Account, string Username, string Secret, AuthenticationMethod AuthenticationMethod)
{
    public string Password => Secret;
}

public sealed class MailCredentialResolver(
    AppDbContext db,
    ICredentialProtector protector,
    IEnumerable<IOAuthProvider> oauthProviders,
    IRuntimePolicyProvider runtimePolicy,
    IPushNotificationService? push = null,
    ILogger<MailCredentialResolver>? logger = null,
    ISyncLockProvider? syncLocks = null,
    MailClientMetrics? metrics = null)
{
    public MailCredentialResolver(AppDbContext db, ICredentialProtector protector)
        : this(db, protector, [], new DefaultRuntimePolicyProvider())
    {
    }

    public MailCredentialResolver(AppDbContext db, ICredentialProtector protector, IEnumerable<IOAuthProvider> oauthProviders)
        : this(db, protector, oauthProviders, new DefaultRuntimePolicyProvider())
    {
    }

    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> RefreshLocks = new();
    private static readonly TimeSpan RefreshWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DistributedLockRetryDelay = TimeSpan.FromMilliseconds(50);

    public async Task<ResolvedCredential> ResolveAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var account = await LoadAccountAsync(accountId, cancellationToken);
        (await runtimePolicy.GetAsync(cancellationToken)).EnsureExistingAccountAllowed(account.Provider);
        var credential = account.Credentials.SingleOrDefault(x => x.AuthenticationMethod == account.AuthenticationMethod)
            ?? throw new InvalidOperationException("credential_missing");
        if (account.AuthenticationMethod != AuthenticationMethod.OAuth2)
            return new ResolvedCredential(account, account.Username, protector.Unprotect(credential.EncryptedMaterial), account.AuthenticationMethod);
        return await ResolveOAuthAsync(accountId, cancellationToken);
    }

    private async Task<ResolvedCredential> ResolveOAuthAsync(Guid accountId, CancellationToken cancellationToken)
    {
        if (await TryFreshTokenAsync(accountId, cancellationToken) is { } fresh)
            return fresh;
        if (syncLocks is not null)
            return await ResolveOAuthWithDistributedLockAsync(accountId, syncLocks, cancellationToken);
        var refreshLock = RefreshLocks.GetOrAdd(accountId, static _ => new SemaphoreSlim(1, 1));
        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            db.ChangeTracker.Clear();
            return await TryFreshTokenAsync(accountId, cancellationToken)
                ?? await RefreshOAuthTokenAsync(accountId, cancellationToken);
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private async Task<ResolvedCredential> ResolveOAuthWithDistributedLockAsync(
        Guid accountId,
        ISyncLockProvider lockProvider,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await using var distributedLock = await lockProvider.TryAcquireAsync(
                accountId,
                SyncLockPurpose.OAuthRefresh,
                cancellationToken);
            db.ChangeTracker.Clear();
            if (await TryFreshTokenAsync(accountId, cancellationToken) is { } fresh)
                return fresh;
            if (distributedLock.IsAcquired)
                return await RefreshOAuthTokenAsync(accountId, cancellationToken);
            if (distributedLock.Status == SyncLockStatus.InfrastructureFailure)
                throw new InvalidOperationException(SyncFailureClassifier.OAuthRefreshLockUnavailable);
            await Task.Delay(DistributedLockRetryDelay, cancellationToken);
        }
    }

    private async Task<ResolvedCredential?> TryFreshTokenAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var account = await LoadAccountAsync(accountId, cancellationToken);
        var credential = account.Credentials.Single(x => x.AuthenticationMethod == AuthenticationMethod.OAuth2);
        var material = Deserialize(credential.EncryptedMaterial);
        if (credential.ExpiresAt is { } expiresAt && expiresAt > DateTime.UtcNow.Add(RefreshWindow))
            return new ResolvedCredential(account, account.Username, material.AccessToken, AuthenticationMethod.OAuth2);
        return null;
    }

    private async Task<ResolvedCredential> RefreshOAuthTokenAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var account = await LoadAccountAsync(accountId, cancellationToken);
        var credential = account.Credentials.Single(x => x.AuthenticationMethod == AuthenticationMethod.OAuth2);
        var material = Deserialize(credential.EncryptedMaterial);
        if (string.IsNullOrWhiteSpace(material.RefreshToken))
            return await RequireReauthenticationAsync(account, cancellationToken);
        var provider = oauthProviders.SingleOrDefault(item => item.Provider == account.Provider && item.IsConfigured)
            ?? throw new InvalidOperationException("oauth_provider_not_configured");
        using var activity = MailClientTelemetry.StartActivity("mailclient.oauth.refresh");
        activity?.SetTag("mail.provider", MailClientTelemetry.Provider(account.Provider));
        var started = Stopwatch.GetTimestamp();
        OAuthToken refreshed;
        try
        {
            refreshed = await provider.RefreshAsync(material.RefreshToken, cancellationToken);
        }
        catch (InvalidOperationException ex) when (ex.Message == "mail_account_needs_reauthentication")
        {
            RecordRefresh(activity, account.Provider, "reauthentication_required", started);
            return await RequireReauthenticationAsync(account, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            MailClientTelemetry.MarkFailed(activity, ex);
            RecordRefresh(activity, account.Provider, "failure", started);
            throw;
        }

        RecordRefresh(activity, account.Provider, "success", started);
        var rotatedRefreshToken = refreshed.RefreshToken ?? material.RefreshToken;
        credential.EncryptedMaterial = protector.Protect(JsonSerializer.Serialize(new OAuthCredentialMaterial(refreshed.AccessToken, rotatedRefreshToken)));
        credential.ExpiresAt = refreshed.ExpiresAt;
        credential.Scopes = refreshed.Scopes;
        credential.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return new ResolvedCredential(account, account.Username, refreshed.AccessToken, AuthenticationMethod.OAuth2);
    }

    private void RecordRefresh(Activity? activity, MailProvider provider, string result, long started)
    {
        activity?.SetTag("oauth.result", result);
        metrics?.RecordOAuthTokenRefresh(provider, result, Stopwatch.GetElapsedTime(started));
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
        var transitioned = account.Status != MailAccountStatus.NeedsReauthentication;
        account.Status = MailAccountStatus.NeedsReauthentication;
        account.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        if (transitioned)
            metrics?.RecordOAuthReauthenticationRequired(account.Provider);
        if (transitioned && push is not null)
        {
            try
            {
                await push.NotifyAsync(new PushEvent(PushEventType.AccountReauthenticationRequired, account.Id), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Reauthentication push failed. Credential state is unaffected.");
            }
        }

        throw new InvalidOperationException("mail_account_needs_reauthentication");
    }
}
