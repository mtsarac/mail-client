using MailClient.Application.Runtime;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Runtime;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Tests;

public sealed class RuntimeSettingsStoreTests
{
    [Fact]
    public async Task Replace_PersistsAndIncrementsVersion()
    {
        await using var db = CreateDb();
        var store = new RuntimeSettingsStore(db);
        var initial = await store.GetAsync(CancellationToken.None);
        var updated = new RuntimeSettings
        {
            Search = new RuntimeSearchSettings { MaxPageSize = 25, MaxQueryLength = 150 }
        };

        var result = await store.ReplaceAsync(initial.Version, updated, CancellationToken.None);

        Assert.Equal(initial.Version + 1, result.Version);
        db.ChangeTracker.Clear();
        var later = await store.GetAsync(CancellationToken.None);
        Assert.Equal(25, later.Settings.Search.MaxPageSize);
        Assert.Equal(result.Version, later.Version);
    }

    [Fact]
    public async Task Replace_RejectsStaleVersionWithoutChangingSettings()
    {
        await using var db = CreateDb();
        var store = new RuntimeSettingsStore(db);
        var initial = await store.GetAsync(CancellationToken.None);
        await store.ReplaceAsync(initial.Version, new RuntimeSettings { Search = new RuntimeSearchSettings { MaxPageSize = 25, MaxQueryLength = 200 } }, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReplaceAsync(initial.Version, new RuntimeSettings(), CancellationToken.None));

        Assert.Equal(RuntimePolicyErrors.RuntimeSettingsConflict, exception.Message);
        db.ChangeTracker.Clear();
        Assert.Equal(25, (await store.GetAsync(CancellationToken.None)).Settings.Search.MaxPageSize);
    }

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options);
}
