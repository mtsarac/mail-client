using MailClient.Application.Sync;

namespace MailClient.Infrastructure.Tests.Sync;

public sealed class SendRequestLimitsTests
{
    [Fact]
    public void DefaultOptions_CeilingCoversAttachmentsPlusBothBodiesPlusOverhead()
    {
        var options = new MailSyncOptions();

        var ceiling = SendRequestLimits.ComputeMaxRequestBytes(options);

        Assert.Equal(
            options.MaxMessageAttachmentBytes + 4L * 2 * options.MaxSendBodyChars + SendRequestLimits.MultipartOverheadBytes,
            ceiling);
        Assert.True(ceiling > options.MaxMessageAttachmentBytes);
    }

    [Fact]
    public void CustomLimits_ScaleCeiling()
    {
        var options = new MailSyncOptions
        {
            MaxMessageAttachmentBytes = 1024,
            MaxSendBodyChars = 100
        };

        Assert.Equal(1024 + 800 + SendRequestLimits.MultipartOverheadBytes,
            SendRequestLimits.ComputeMaxRequestBytes(options));
    }

    [Fact]
    public void InvalidBodyCap_RejectedAtStartup()
    {
        var options = new MailSyncOptions { MaxSendBodyChars = 0 };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }
}
