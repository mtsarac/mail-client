using MailClient.Domain.Enums;

namespace MailClient.Application.Runtime;

public sealed class RuntimeSettings
{
    public RuntimeProviderSettings Providers { get; init; } = new();
    public RuntimeSyncSettings Sync { get; init; } = new();
    public RuntimeLimitSettings Limits { get; init; } = new();
    public RuntimeSearchSettings Search { get; init; } = new();

    public void Validate()
    {
        if (Sync.PollIntervalSeconds <= 0
            || Sync.FlagSyncIntervalSeconds <= 0
            || Sync.MaxMessagesPerRun <= 0
            || Limits.MaxAttachmentBytes <= 0
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

public sealed class DefaultRuntimePolicyProvider : IRuntimePolicyProvider
{
    public Task<RuntimeProviderPolicy> GetAsync(CancellationToken cancellationToken) =>
        Task.FromResult(RuntimeProviderPolicy.Create(new RuntimeSettings(), new ProviderCapabilities(false, false)));
}

public static class RuntimePolicyErrors
{
    public const string RuntimeSettingsInvalid = "runtime_settings_invalid";
    public const string RuntimeSettingsConflict = "runtime_settings_conflict";
    public const string ProviderDisabled = "provider_disabled";
    public const string ProviderNewAccountsDisabled = "provider_new_accounts_disabled";
    public const string ProviderExistingAccountsDisabled = "provider_existing_accounts_disabled";
    public const string AuthenticationMethodDisabled = "authentication_method_disabled";
}
