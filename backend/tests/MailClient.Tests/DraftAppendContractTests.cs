using MailClient.Infrastructure.Email;
using MailKit;
using MimeKit;

namespace MailClient.Tests;

public sealed class DraftAppendContractTests
{
    [Fact]
    public async Task AppendAsync_RecordsDraftFlagsAndServerIdentifiers()
    {
        var remote = new FakeRemoteMailFolder(77, new())
        {
            AppendResult = new RemoteAppendResult(new UniqueId(42), 77)
        };
        var message = new MimeMessage { Subject = "draft" };

        var result = await remote.AppendAsync(message, MessageFlags.Draft, CancellationToken.None);

        Assert.Equal(MessageFlags.Draft, remote.LastAppendFlags);
        Assert.Equal(42u, result.DestinationUid?.Id);
        Assert.Equal(77u, result.DestinationUidValidity);
    }

    [Fact]
    public async Task AppendAsync_DoesNotFabricateMissingUid()
    {
        var remote = new FakeRemoteMailFolder(77, new())
        {
            AppendResult = new RemoteAppendResult(null, 77)
        };

        var result = await remote.AppendAsync(new MimeMessage(), MessageFlags.Draft, CancellationToken.None);

        Assert.Null(result.DestinationUid);
        Assert.Equal(77u, result.DestinationUidValidity);
    }
}
