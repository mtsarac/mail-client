using MailClient.Application.Discovery;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;

namespace MailClient.Tests;

public sealed class DomainModelTests
{
    [Fact]
    public void MailAccount_IsPrincipalWithoutUserOwnership()
    {
        var properties = typeof(MailAccount).GetProperties().Select(property => property.Name).ToArray();
        Assert.DoesNotContain("UserId", properties);
        Assert.Contains("NormalizedEmailAddress", properties);
        Assert.Contains("Credentials", properties);
        Assert.Contains("Sessions", properties);
    }

    [Fact]
    public async Task Discovery_UsesDeterministicOrderAndFirstValidatedCandidate()
    {
        List<string> calls = [];
        IMailDiscoveryStrategy[] strategies =
        [
            new FakeStrategy("catalog", calls, null),
            new FakeStrategy("srv", calls, Candidate("srv.example.test")),
            new FakeStrategy("autoconfig", calls, Candidate("unused.example.test"))
        ];
        var service = new MailServerDiscoveryService(strategies, new AcceptingValidator(), new MailDiscoveryOptions());

        var result = await service.DiscoverAsync("person@example.test", CancellationToken.None);

        Assert.Equal("srv.example.test", result!.Imap.Host);
        Assert.Equal(["catalog", "srv"], calls);
    }

    [Theory]
    [InlineData(FolderSyncScope.InboxAndSent, MailFolderType.Inbox, true)]
    [InlineData(FolderSyncScope.InboxAndSent, MailFolderType.Sent, true)]
    [InlineData(FolderSyncScope.InboxAndSent, MailFolderType.Custom, false)]
    [InlineData(FolderSyncScope.AllFolders, MailFolderType.Custom, true)]
    [InlineData(FolderSyncScope.AllFolders, MailFolderType.Trash, true)]
    [InlineData(FolderSyncScope.SelectedFolders, MailFolderType.Inbox, false)]
    [InlineData(FolderSyncScope.SelectedFolders, MailFolderType.Custom, false)]
    public void NewlyDiscoveredFolder_FollowsAccountSyncScope(FolderSyncScope scope, MailFolderType type, bool expected) =>
        Assert.Equal(expected, MailFolder.SyncedByDefault(scope, type));

    private static MailServerCandidate Candidate(string host) => new(
        MailProvider.Custom,
        new MailEndpoint(host, 993, MailSecurity.SslOnConnect),
        new MailEndpoint("smtp.example.test", 465, MailSecurity.SslOnConnect),
        [AuthenticationMethod.Password],
        DiscoverySource.DnsSrv);

    private sealed class FakeStrategy(string name, List<string> calls, MailServerCandidate? candidate) : IMailDiscoveryStrategy
    {
        public int Order => name switch { "catalog" => 1, "srv" => 2, _ => 3 };
        public async IAsyncEnumerable<MailServerCandidate> DiscoverAsync(string email, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            calls.Add(name);
            if (candidate is not null) yield return candidate;
            await Task.CompletedTask;
        }
    }

    private sealed class AcceptingValidator : IMailServerCandidateValidator
    {
        public Task<bool> ValidateAsync(MailServerCandidate candidate, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
