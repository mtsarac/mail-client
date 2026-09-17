using MailClient.Application.Runtime;

namespace MailClient.Infrastructure.Runtime;

public sealed class DefaultRuntimeSettingsStore : IRuntimeSettingsStore
{
    public Task<RuntimeSettingsSnapshot> GetAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new RuntimeSettingsSnapshot(new RuntimeSettings(), 1, DateTime.UtcNow));

    public Task<RuntimeSettingsSnapshot> ReplaceAsync(int expectedVersion, RuntimeSettings settings, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
