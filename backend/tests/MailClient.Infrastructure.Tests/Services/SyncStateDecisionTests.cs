using MailClient.Infrastructure.Services;
using MailClient.Application.Sync;

namespace MailClient.Infrastructure.Tests.Services;

public sealed class SyncStateDecisionTests
{
    [Fact]
    public void Validate_RejectsMessageLimitBelowAttachmentLimit()
    {
        var options = new MailSyncOptions
        {
            MaxAttachmentBytes = 10,
            MaxMessageAttachmentBytes = 9
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Validate_AcceptsDefaultOptions()
    {
        new MailSyncOptions().Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_RejectsNonPositiveMaxMessageBytes(long maxMessageBytes)
    {
        var options = new MailSyncOptions { MaxMessageBytes = maxMessageBytes };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Validate_RejectsMaxMessageBytesBelowAttachmentBudget()
    {
        var options = new MailSyncOptions
        {
            MaxMessageAttachmentBytes = 100,
            MaxMessageBytes = 99
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }
    [Theory]
    [InlineData(0u, 10u, false)]
    [InlineData(10u, 10u, false)]
    [InlineData(10u, 11u, true)]
    public void RequiresReset_ReturnsTrueOnlyForChangedKnownUidValidity(
        uint storedUidValidity,
        uint serverUidValidity,
        bool expected)
    {
        Assert.Equal(expected, SyncStateDecision.RequiresReset(storedUidValidity, serverUidValidity));
    }
}
