using MailClient.Domain.Enums;

namespace MailClient.Application.Runtime;

public sealed class RuntimeProviderPolicy(RuntimeSettings settings, ProviderCapabilities capabilities)
{

    public IReadOnlyList<AuthenticationMethod> GetAuthenticationMethods(MailProvider provider)
    {
        var policy = settings.Providers.Get(provider);
        if (!policy.Enabled)
        {
            return [];
        }

        var methods = new List<AuthenticationMethod>();
        if (policy.PasswordEnabled)
        {
            methods.Add(AuthenticationMethod.Password);
        }

        if (policy.AppSpecificPasswordEnabled)
        {
            methods.Add(AuthenticationMethod.AppSpecificPassword);
        }

        if (policy.OAuth2Enabled && SupportsOAuth2(provider))
        {
            methods.Add(AuthenticationMethod.OAuth2);
        }

        return methods;
    }

    public void EnsureNewAccountAllowed(MailProvider provider, AuthenticationMethod authenticationMethod)
    {
        var policy = settings.Providers.Get(provider);
        if (!policy.Enabled)
        {
            throw new InvalidOperationException(RuntimePolicyErrors.ProviderDisabled);
        }

        if (!policy.AllowNewAccounts)
        {
            throw new InvalidOperationException(RuntimePolicyErrors.ProviderNewAccountsDisabled);
        }

        EnsureAuthenticationMethodAllowed(provider, authenticationMethod);
    }

    public void EnsureExistingAccountAllowed(MailProvider provider)
    {
        var policy = settings.Providers.Get(provider);
        if (!policy.Enabled || !policy.AllowExistingAccounts)
        {
            throw new InvalidOperationException(RuntimePolicyErrors.ProviderExistingAccountsDisabled);
        }
    }

    public void EnsureAuthenticationMethodAllowed(MailProvider provider, AuthenticationMethod authenticationMethod)
    {
        if (!GetAuthenticationMethods(provider).Contains(authenticationMethod))
        {
            throw new InvalidOperationException(RuntimePolicyErrors.AuthenticationMethodDisabled);
        }
    }

    private bool SupportsOAuth2(MailProvider provider) => provider switch
    {
        MailProvider.Google => capabilities.GoogleOAuth2,
        MailProvider.Microsoft => capabilities.MicrosoftOAuth2,
        _ => false
    };
}
