using MailClient.Application.Runtime;
using MailClient.Infrastructure.OAuth;

namespace MailClient.Infrastructure.Runtime;

public sealed class RuntimePolicyProvider(
    IRuntimeSettingsStore settingsStore,
    IEnumerable<IOAuthProvider> oauthProviders) : IRuntimePolicyProvider
{
    public async Task<RuntimeProviderPolicy> GetAsync(CancellationToken cancellationToken)
    {
        var snapshot = await settingsStore.GetAsync(cancellationToken);
        return RuntimeProviderPolicy.Create(snapshot.Settings, new ProviderCapabilities(
            oauthProviders.Any(provider => provider.Provider == Domain.Enums.MailProvider.Google && provider.IsConfigured),
            oauthProviders.Any(provider => provider.Provider == Domain.Enums.MailProvider.Microsoft && provider.IsConfigured)));
    }
}
