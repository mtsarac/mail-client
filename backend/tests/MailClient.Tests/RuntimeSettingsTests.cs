using MailClient.Application.Runtime;
using MailClient.Domain.Enums;

namespace MailClient.Tests;

public sealed class RuntimeSettingsTests
{
    [Fact]
    public void Defaults_ReproduceCurrentOperationalBehavior()
    {
        var settings = new RuntimeSettings();

        Assert.True(settings.Sync.Enabled);
        Assert.Equal(30, settings.Sync.PollIntervalSeconds);
        Assert.Equal(120, settings.Sync.FlagSyncIntervalSeconds);
        Assert.Equal(100, settings.Sync.MaxMessagesPerRun);
        Assert.Equal(25 * 1024 * 1024, settings.Limits.MaxAttachmentBytes);
        Assert.Equal(50 * 1024 * 1024, settings.Limits.MaxMessageAttachmentBytes);
        Assert.Equal(100 * 1024 * 1024, settings.Limits.MaxMessageBytes);
        Assert.Equal(1_000_000, settings.Limits.MaxSendBodyChars);
        Assert.Equal(100, settings.Search.MaxPageSize);
        Assert.Equal(200, settings.Search.MaxQueryLength);
        Assert.True(settings.Providers.Get(MailProvider.Google).Enabled);
        Assert.False(settings.Providers.Get(MailProvider.Custom).OAuth2Enabled);
    }

    [Fact]
    public void Validate_RejectsAttachmentLimitInversion()
    {
        var settings = new RuntimeSettings
        {
            Limits = new RuntimeLimitSettings
            {
                MaxAttachmentBytes = 100,
                MaxMessageAttachmentBytes = 99,
                MaxMessageBytes = 1000,
                MaxSendBodyChars = 100
            }
        };

        var exception = Assert.Throws<InvalidOperationException>(settings.Validate);

        Assert.Equal("runtime_settings_invalid", exception.Message);
    }

    [Fact]
    public void ProviderPolicy_CannotEnableUnsupportedOAuth()
    {
        var policy = RuntimeProviderPolicy.Create(
            new RuntimeSettings(),
            new ProviderCapabilities(GoogleOAuth2: true, MicrosoftOAuth2: true));

        Assert.DoesNotContain(AuthenticationMethod.OAuth2, policy.GetAuthenticationMethods(MailProvider.Custom));
        Assert.DoesNotContain(AuthenticationMethod.OAuth2, policy.GetAuthenticationMethods(MailProvider.ICloud));
        Assert.Contains(AuthenticationMethod.OAuth2, policy.GetAuthenticationMethods(MailProvider.Google));
    }
}
