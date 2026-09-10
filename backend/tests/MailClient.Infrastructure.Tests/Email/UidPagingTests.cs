using MailClient.Infrastructure.Email;
using MailKit;

namespace MailClient.Infrastructure.Tests.Email;

public sealed class UidPagingTests
{
    [Fact]
    public async Task DenseBacklog_ReturnsBoundedAscending_WithSingleSearch()
    {
        var script = Script(Enumerable.Range(1, 10000).Select(uid => (uint)uid), uidNext: 10001);

        var found = await MailKitRemoteMailFolder.SearchPagedAsync(
            0, 100, script.UidNext, script.SearchPage, CancellationToken.None);

        Assert.Equal(100, found.Uids.Count);
        Assert.Equal(Enumerable.Range(1, 100).Select(uid => (uint)uid), found.Uids.Select(uid => uid.Id));
        Assert.Equal(1, script.Calls);
    }

    [Fact]
    public async Task SparseGap_BoundsSearchCalls_AndFindsBoth()
    {
        var script = Script([150, 900000], uidNext: 1000000);

        var found = await MailKitRemoteMailFolder.SearchPagedAsync(
            100, 100, script.UidNext, script.SearchPage, CancellationToken.None);

        Assert.Equal([150u, 900000u], found.Uids.Select(uid => uid.Id));
        Assert.True(script.Calls <= 8);
    }

    [Fact]
    public async Task LargeUidNextWithoutMail_StopsBounded()
    {
        var script = Script([], uidNext: 1000000);

        var found = await MailKitRemoteMailFolder.SearchPagedAsync(
            0, 100, script.UidNext, script.SearchPage, CancellationToken.None);

        Assert.Empty(found.Uids);
        Assert.True(script.Calls <= 8);
    }

    [Fact]
    public async Task ResumeAfterCheckpoint_FindsRemaining()
    {
        var script = Script([150, 900000], uidNext: 1000000);

        var found = await MailKitRemoteMailFolder.SearchPagedAsync(
            150, 100, script.UidNext, script.SearchPage, CancellationToken.None);

        Assert.Equal([900000u], found.Uids.Select(uid => uid.Id));
    }

    [Fact]
    public async Task UintBoundary_HandledWithoutOverflow()
    {
        var atMax = Script([uint.MaxValue - 1], uidNext: uint.MaxValue);
        var found = await MailKitRemoteMailFolder.SearchPagedAsync(
            uint.MaxValue - 2, 100, atMax.UidNext, atMax.SearchPage, CancellationToken.None);
        Assert.Equal([uint.MaxValue - 1], found.Uids.Select(uid => uid.Id));

        var pastMax = Script([uint.MaxValue], uidNext: uint.MaxValue);
        Assert.Empty((await MailKitRemoteMailFolder.SearchPagedAsync(
            uint.MaxValue, 100, pastMax.UidNext, pastMax.SearchPage, CancellationToken.None)).Uids);
        Assert.Equal(0, pastMax.Calls);
    }

    [Fact]
    public async Task Cancellation_DuringPaging_Propagates()
    {
        var script = Script([150, 900000], uidNext: 1000000);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MailKitRemoteMailFolder.SearchPagedAsync(100, 100, script.UidNext, script.SearchPage, cts.Token));
    }

    [Fact]
    public async Task Cancellation_MidPaging_Propagates()
    {
        using var cts = new CancellationTokenSource();
        var script = Script([150, 900000], uidNext: 1000000);
        Task<IList<UniqueId>> Paged(ulong low, ulong high, CancellationToken ct)
        {
            cts.Cancel();
            return script.SearchPage(low, high, ct);
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MailKitRemoteMailFolder.SearchPagedAsync(100, 100, script.UidNext, Paged, cts.Token));
    }

    [Fact]
    public async Task Results_NeverExceedMax_AndContainNoDuplicates()
    {
        var script = Script(Enumerable.Range(1, 1000).Select(uid => (uint)uid), uidNext: 1001);

        var found = await MailKitRemoteMailFolder.SearchPagedAsync(
            0, 50, script.UidNext, script.SearchPage, CancellationToken.None);

        Assert.Equal(50, found.Uids.Count);
        Assert.Equal(50, found.Uids.Select(uid => uid.Id).Distinct().Count());
    }

    private static ScriptedMailbox Script(IEnumerable<uint> existing, uint uidNext) =>
        new(existing.OrderBy(uid => uid).ToList(), uidNext);

    private sealed class ScriptedMailbox(List<uint> existing, uint uidNext)
    {
        public int Calls { get; private set; }
        public uint UidNext() => uidNext;

        public Task<IList<UniqueId>> SearchPage(ulong low, ulong high, CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            IList<UniqueId> page = existing
                .Where(uid => uid >= low && uid <= high)
                .Select(uid => new UniqueId(uid))
                .ToList();
            return Task.FromResult(page);
        }
    }
}
