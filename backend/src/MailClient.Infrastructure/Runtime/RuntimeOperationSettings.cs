using MailClient.Application.Runtime;

namespace MailClient.Infrastructure.Runtime;

/// <summary>Runtime settings read once per scope, so one sync run or request sees a consistent snapshot.</summary>
public sealed class RuntimeOperationSettings(IRuntimeSettingsStore store)
{
    private RuntimeSettingsSnapshot? snapshot;

    /// <summary>The settings loaded by <see cref="GetAsync"/>; never silently replaced with defaults.</summary>
    public RuntimeSettings Current =>
        snapshot?.Settings ?? throw new InvalidOperationException("Runtime settings have not been loaded for this scope.");

    public async Task<RuntimeSettingsSnapshot> GetAsync(CancellationToken cancellationToken) =>
        snapshot ??= await store.GetAsync(cancellationToken);
}
