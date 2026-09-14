using MailClient.Application.Authentication;
using MailClient.Domain.Entities;
using MailClient.Infrastructure.Authentication;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Tests;

public sealed class MailSessionServiceTests
{
    [Fact]
    public async Task Rotate_RevokesOldSessionAndStoresOnlyHashes()
    {
        await using var db = CreateDb();
        var account = new MailAccount { Id = Guid.NewGuid(), EmailAddress = "a@example.test", NormalizedEmailAddress = "A@EXAMPLE.TEST" };
        db.MailAccounts.Add(account);
        await db.SaveChangesAsync();
        var service = new MailSessionService(db, new SessionOptions());
        var created = await service.CreateAsync(account.Id, "phone", TimeSpan.FromDays(30), CancellationToken.None);

        var rotated = await service.RotateAsync(created.Token, TimeSpan.FromDays(30), true, CancellationToken.None);

        Assert.NotNull(rotated);
        Assert.NotEqual(created.Token, rotated.Value.Token);
        Assert.DoesNotContain(await db.MailSessions.ToListAsync(), session => session.RefreshTokenHash == created.Token || session.RefreshTokenHash == rotated.Value.Token);
        Assert.NotNull((await db.MailSessions.FindAsync(created.Session.Id))!.RevokedAt);
        Assert.Equal(rotated.Value.Session.Id, (await db.MailSessions.FindAsync(created.Session.Id))!.ReplacedBySessionId);
        Assert.Null(await service.RotateAsync(created.Token, TimeSpan.FromDays(30), true, CancellationToken.None));
    }

    [Fact]
    public async Task Rotate_SlidingExpiration_RenewsLifetime()
    {
        await using var db = CreateDb();
        var account = await SeedAccountAsync(db);
        var service = new MailSessionService(db, new SessionOptions { SlidingExpiration = true });
        var created = await service.CreateAsync(account.Id, null, TimeSpan.FromMinutes(5), CancellationToken.None);

        var rotated = await service.RotateAsync(created.Token, CancellationToken.None);

        Assert.NotNull(rotated);
        Assert.True(rotated.Value.Session.ExpiresAt > created.Session.ExpiresAt.AddDays(100));
    }

    [Fact]
    public async Task Rotate_FixedLifetime_KeepsOriginalAbsoluteExpiry()
    {
        await using var db = CreateDb();
        var account = await SeedAccountAsync(db);
        var service = new MailSessionService(db, new SessionOptions { SlidingExpiration = false });
        var created = await service.CreateAsync(account.Id, null, TimeSpan.FromDays(30), CancellationToken.None);

        var rotated = await service.RotateAsync(created.Token, CancellationToken.None);

        Assert.NotNull(rotated);
        Assert.True(Math.Abs((rotated.Value.Session.ExpiresAt - created.Session.ExpiresAt).TotalSeconds) < 1);
    }

    [Fact]
    public async Task Rotate_ExpiredSession_ReturnsNull()
    {
        await using var db = CreateDb();
        var account = await SeedAccountAsync(db);
        var service = new MailSessionService(db, new SessionOptions());
        var created = await service.CreateAsync(account.Id, null, TimeSpan.FromMinutes(-1), CancellationToken.None);

        Assert.Null(await service.RotateAsync(created.Token, CancellationToken.None));
    }

    [Fact]
    public async Task Rotate_RevokedSession_ReturnsNull()
    {
        await using var db = CreateDb();
        var account = await SeedAccountAsync(db);
        var service = new MailSessionService(db, new SessionOptions());
        var created = await service.CreateAsync(account.Id, null, CancellationToken.None);

        Assert.True(await service.RevokeAsync(created.Token, CancellationToken.None));

        Assert.Null(await service.RotateAsync(created.Token, CancellationToken.None));
    }

    private static async Task<MailAccount> SeedAccountAsync(AppDbContext db)
    {
        var account = new MailAccount { Id = Guid.NewGuid(), EmailAddress = "a@example.test", NormalizedEmailAddress = "A@EXAMPLE.TEST" };
        db.MailAccounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
