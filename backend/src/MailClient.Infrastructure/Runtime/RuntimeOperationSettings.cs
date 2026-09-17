using MailClient.Application.Runtime;
using MailClient.Application.Sync;

namespace MailClient.Infrastructure.Runtime;

public sealed class RuntimeOperationSettings(IRuntimeSettingsStore store)
{
    private RuntimeSettingsSnapshot? snapshot;
    public MailSyncOptions Current => ToMailSyncOptions(snapshot?.Settings ?? new RuntimeSettings());

    public async Task<RuntimeSettingsSnapshot> GetAsync(CancellationToken cancellationToken) =>
        snapshot ??= await store.GetAsync(cancellationToken);

    public void Set(RuntimeSettingsSnapshot value) => snapshot = value;

    public static RuntimeOperationSettings FromMailSyncOptions(MailSyncOptions options)
    {
        var settings = new RuntimeSettings
        {
            Sync = new RuntimeSyncSettings
            {
                Enabled = options.Enabled,
                PollIntervalSeconds = options.PollIntervalSeconds,
                FlagSyncIntervalSeconds = options.FlagSyncIntervalSeconds,
                MaxMessagesPerRun = options.MaxMessagesPerRun
            },
            Limits = new RuntimeLimitSettings
            {
                MaxAttachmentBytes = options.MaxAttachmentBytes,
                MaxMessageAttachmentBytes = options.MaxMessageAttachmentBytes,
                MaxMessageBytes = options.MaxMessageBytes,
                MaxSendBodyChars = options.MaxSendBodyChars
            }
        };
        var operationSettings = new RuntimeOperationSettings(new DefaultRuntimeSettingsStore());
        operationSettings.Set(new RuntimeSettingsSnapshot(settings, 1, DateTime.UtcNow));
        return operationSettings;
    }

    public static MailSyncOptions ToMailSyncOptions(RuntimeSettings settings) => new()
    {
        Enabled = settings.Sync.Enabled,
        PollIntervalSeconds = settings.Sync.PollIntervalSeconds,
        FlagSyncIntervalSeconds = settings.Sync.FlagSyncIntervalSeconds,
        MaxMessagesPerRun = settings.Sync.MaxMessagesPerRun,
        MaxAttachmentBytes = settings.Limits.MaxAttachmentBytes,
        MaxMessageAttachmentBytes = settings.Limits.MaxMessageAttachmentBytes,
        MaxMessageBytes = settings.Limits.MaxMessageBytes,
        MaxSendBodyChars = settings.Limits.MaxSendBodyChars
    };
}
