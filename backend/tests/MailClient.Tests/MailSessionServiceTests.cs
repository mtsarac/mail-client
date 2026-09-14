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
        var service = new MailSessionService(db);
        var created = await service.CreateAsync(account.Id, "phone", TimeSpan.FromDays(30), CancellationToken.None);

        var rotated = await service.RotateAsync(created.Token, TimeSpan.FromDays(30), CancellationToken.None);

        Assert.NotNull(rotated);
        Assert.NotEqual(created.Token, rotated.Value.Token);
        Assert.DoesNotContain(await db.MailSessions.ToListAsync(), session => session.RefreshTokenHash == created.Token || session.RefreshTokenHash == rotated.Value.Token);
        Assert.NotNull((await db.MailSessions.FindAsync(created.Session.Id))!.RevokedAt);
        Assert.Equal(rotated.Value.Session.Id, (await db.MailSessions.FindAsync(created.Session.Id))!.ReplacedBySessionId);
        Assert.Null(await service.RotateAsync(created.Token, TimeSpan.FromDays(30), CancellationToken.None));
    }

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
