using MailClient.Domain.Enums;

namespace MailClient.Application.Runtime;

public sealed class RuntimeSettings
{
    public RuntimeProviderSettings Providers { get; init; } = new();
    public RuntimeSyncSettings Sync { get; init; } = new();
    public RuntimeLimitSettings Limits { get; init; } = new();
    public RuntimeSearchSettings Search { get; init; } = new();
    public RuntimePushSettings Push { get; init; } = new();
    public RuntimeWhitelistSettings Whitelist { get; init; } = new();

    public void Validate()
    {
        Sync.Validate();
        Whitelist.Validate();
        if (Limits.MaxAttachmentBytes <= 0
            || Limits.MaxMessageAttachmentBytes < Limits.MaxAttachmentBytes
            || Limits.MaxMessageBytes <= 0
            || Limits.MaxMessageBytes < Limits.MaxMessageAttachmentBytes
            || Limits.MaxSendBodyChars <= 0
            || Search.MaxPageSize <= 0
            || Search.MaxQueryLength <= 0)
        {
            throw new InvalidOperationException(RuntimePolicyErrors.RuntimeSettingsInvalid);
        }
    }
}

public sealed class RuntimeProviderSettings
{
    public RuntimeProviderPolicySettings Google { get; init; } = RuntimeProviderPolicySettings.Default();
    public RuntimeProviderPolicySettings Microsoft { get; init; } = RuntimeProviderPolicySettings.Default();
    public RuntimeProviderPolicySettings ICloud { get; init; } = RuntimeProviderPolicySettings.Default(oAuth2Enabled: false);
    public RuntimeProviderPolicySettings Yahoo { get; init; } = RuntimeProviderPolicySettings.Default(oAuth2Enabled: false);
    public RuntimeProviderPolicySettings Custom { get; init; } = RuntimeProviderPolicySettings.Default(oAuth2Enabled: false);

    public RuntimeProviderPolicySettings Get(MailProvider provider) => provider switch
    {
        MailProvider.Google => Google,
        MailProvider.Microsoft => Microsoft,
        MailProvider.ICloud => ICloud,
        MailProvider.Yahoo => Yahoo,
        MailProvider.Custom => Custom,
        _ => throw new InvalidOperationException(RuntimePolicyErrors.ProviderDisabled)
    };
}

public sealed class RuntimeProviderPolicySettings
{
    public bool Enabled { get; init; } = true;
    public bool AllowNewAccounts { get; init; } = true;
    public bool AllowExistingAccounts { get; init; } = true;
    public bool PasswordEnabled { get; init; } = true;
    public bool AppSpecificPasswordEnabled { get; init; } = true;
    public bool OAuth2Enabled { get; init; } = true;

    public static RuntimeProviderPolicySettings Default(bool oAuth2Enabled = true) => new() { OAuth2Enabled = oAuth2Enabled };
}

public sealed class RuntimeSyncSettings
{
    public bool Enabled { get; init; } = true;
    public int PollIntervalSeconds { get; init; } = 30;
    public int FlagSyncIntervalSeconds { get; init; } = 120;
    public int MaxMessagesPerRun { get; init; } = 100;
    public int MaxConcurrentAccounts { get; init; } = 4;
    public int MaxConcurrentFoldersPerAccount { get; init; } = 1;
    public int QueueCapacity { get; init; } = 1000;
    public int TransientRetryMaxAttempts { get; init; } = 3;
    public int RetryBaseDelaySeconds { get; init; } = 2;
    public int RetryMaxDelaySeconds { get; init; } = 60;
    public int MaxConcurrentSyncConnectionsPerHost { get; init; } = 4;
    public int SyncErrorFailureThreshold { get; init; } = 3;

    public void Validate()
    {
        if (PollIntervalSeconds <= 0
            || FlagSyncIntervalSeconds <= 0
            || MaxMessagesPerRun <= 0
            || MaxConcurrentAccounts <= 0
            || MaxConcurrentFoldersPerAccount <= 0
            || QueueCapacity <= 0
            || TransientRetryMaxAttempts <= 0
            || RetryBaseDelaySeconds <= 0
            || RetryMaxDelaySeconds <= 0
            || RetryBaseDelaySeconds > RetryMaxDelaySeconds
            || MaxConcurrentSyncConnectionsPerHost <= 0
            || SyncErrorFailureThreshold <= 0)
        {
            throw new InvalidOperationException(RuntimePolicyErrors.RuntimeSettingsInvalid);
        }
    }
}

public sealed class RuntimeLimitSettings
{
    public long MaxAttachmentBytes { get; init; } = 25 * 1024 * 1024;
    public long MaxMessageAttachmentBytes { get; init; } = 50 * 1024 * 1024;
    public long MaxMessageBytes { get; init; } = 100 * 1024 * 1024;
    public int MaxSendBodyChars { get; init; } = 1_000_000;
}

public sealed class RuntimeSearchSettings
{
    public int MaxPageSize { get; init; } = 100;
    public int MaxQueryLength { get; init; } = 200;
}

public sealed class RuntimePushSettings
{
    public bool Enabled { get; init; } = true;
    public bool NewMailEnabled { get; init; } = true;
    public bool MailStateChangedEnabled { get; init; } = true;
    public bool ReauthenticationEnabled { get; init; } = true;
    public bool SyncErrorEnabled { get; init; } = true;
    public bool IncludeMailPreview { get; init; } = true;
}

public sealed class RuntimeWhitelistSettings
{
    /// <summary>Gates account creation and continued access to a configured email allowlist. Off by default; forced off outside Production.</summary>
    public bool Enabled { get; init; } = false;
    public int ReconciliationIntervalMinutes { get; init; } = 15;
    public int DataRetentionGraceDays { get; init; } = 30;

    public void Validate()
    {
        if (ReconciliationIntervalMinutes <= 0 || DataRetentionGraceDays <= 0)
            throw new InvalidOperationException(RuntimePolicyErrors.RuntimeSettingsInvalid);
    }
}

public sealed record RuntimeSettingsSnapshot(RuntimeSettings Settings, int Version, DateTime UpdatedAt);
public sealed record ProviderCapabilities(bool GoogleOAuth2, bool MicrosoftOAuth2);

public interface IRuntimeSettingsStore
{
    Task<RuntimeSettingsSnapshot> GetAsync(CancellationToken cancellationToken);
    Task<RuntimeSettingsSnapshot> ReplaceAsync(int expectedVersion, RuntimeSettings settings, CancellationToken cancellationToken);
}

public interface IRuntimePolicyProvider
{
    Task<RuntimeProviderPolicy> GetAsync(CancellationToken cancellationToken);
}

public interface IEmailAllowlistService
{
    /// <summary>True when the allowlist is disabled, or the email is on it. Always true outside Production.</summary>
    Task<bool> IsAllowedAsync(string email, CancellationToken cancellationToken);

    /// <summary>True only when allowlist side effects (rejecting signups, disabling/restoring accounts,
    /// grace-period deletion) should actually apply right now: enabled, and running in Production.</summary>
    Task<bool> IsEnforcedAsync(CancellationToken cancellationToken);
}

public sealed class DefaultRuntimePolicyProvider : IRuntimePolicyProvider
{
    public Task<RuntimeProviderPolicy> GetAsync(CancellationToken cancellationToken) =>
        Task.FromResult(RuntimeProviderPolicy.Create(new RuntimeSettings(), new ProviderCapabilities(false, false)));
}

public sealed class DefaultEmailAllowlistService : IEmailAllowlistService
{
    public Task<bool> IsAllowedAsync(string email, CancellationToken cancellationToken) => Task.FromResult(true);
    public Task<bool> IsEnforcedAsync(CancellationToken cancellationToken) => Task.FromResult(false);
}

public static class RuntimePolicyErrors
{
    public const string RuntimeSettingsInvalid = "runtime_settings_invalid";
    public const string RuntimeSettingsConflict = "runtime_settings_conflict";
    public const string ProviderDisabled = "provider_disabled";
    public const string ProviderNewAccountsDisabled = "provider_new_accounts_disabled";
    public const string ProviderExistingAccountsDisabled = "provider_existing_accounts_disabled";
    public const string AuthenticationMethodDisabled = "authentication_method_disabled";
    public const string EmailNotAllowlisted = "email_not_allowlisted";
}
