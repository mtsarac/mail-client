using MailClient.Infrastructure.Services;

namespace MailClient.Infrastructure.Tests.Services;

public sealed class SyncStateDecisionTests
{
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
