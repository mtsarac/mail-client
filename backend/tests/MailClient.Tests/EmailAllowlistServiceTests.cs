using MailClient.Application.Runtime;
using MailClient.Domain.Entities;
using MailClient.Infrastructure.Accounts;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Runtime;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Tests;

public sealed class EmailAllowlistServiceTests
{
    [Fact]
    public async Task DisabledWhitelist_AllowsEveryone()
    {
        await using var db = CreateDb();
        var service = new EmailAllowlistService(db, new RuntimeSettingsStore(db));

        Assert.True(await service.IsAllowedAsync("stranger@example.com", CancellationToken.None));
        Assert.False(await service.IsEnforcedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task EnabledWhitelist_RejectsUnlistedEmail()
    {
        await using var db = CreateDb();
        await SetWhitelistEnabledAsync(db, enabled: true);
        var service = new EmailAllowlistService(db, new RuntimeSettingsStore(db));

        Assert.False(await service.IsAllowedAsync("stranger@example.com", CancellationToken.None));
        Assert.True(await service.IsEnforcedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task EnabledWhitelist_AllowsListedEmail_CaseAndWhitespaceInsensitive()
    {
        await using var db = CreateDb();
        await SetWhitelistEnabledAsync(db, enabled: true);
        db.AllowlistedEmails.Add(new AllowlistedEmail { Id = Guid.NewGuid(), Email = "person@example.com", NormalizedEmail = "PERSON@EXAMPLE.COM", AddedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(CancellationToken.None);
        var service = new EmailAllowlistService(db, new RuntimeSettingsStore(db));

        Assert.True(await service.IsAllowedAsync("  Person@Example.com  ", CancellationToken.None));
    }

    private static async Task SetWhitelistEnabledAsync(AppDbContext db, bool enabled)
    {
        var store = new RuntimeSettingsStore(db);
        var current = await store.GetAsync(CancellationToken.None);
        await store.ReplaceAsync(current.Version, new RuntimeSettings { Whitelist = new RuntimeWhitelistSettings { Enabled = enabled } }, CancellationToken.None);
        db.ChangeTracker.Clear();
    }

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options);
}
