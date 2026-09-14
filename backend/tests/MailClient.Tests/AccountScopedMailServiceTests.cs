using MailClient.Domain.Entities;
using MailClient.Infrastructure.Mail;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Tests;

public sealed class AccountScopedMailServiceTests
{
    [Fact]
    public async Task GetMail_ReturnsNullForDifferentAccount()
    {
        await using var db = CreateDb();
        var owner = Guid.NewGuid();
        var mail = new Domain.Entities.Mail { Id = Guid.NewGuid(), MailAccountId = owner, Subject = "private" };
        db.Mails.Add(mail);
        await db.SaveChangesAsync();

        var service = new AccountScopedMailService(db);

        Assert.Null(await service.GetAsync(Guid.NewGuid(), mail.Id, CancellationToken.None));
        Assert.NotNull(await service.GetAsync(owner, mail.Id, CancellationToken.None));
    }

    [Fact]
    public async Task SetRead_DoesNotChangeDifferentAccountMail()
    {
        await using var db = CreateDb();
        var mail = new Domain.Entities.Mail { Id = Guid.NewGuid(), MailAccountId = Guid.NewGuid(), IsRead = false };
        db.Mails.Add(mail);
        await db.SaveChangesAsync();

        Assert.False(await new AccountScopedMailService(db).SetReadAsync(Guid.NewGuid(), mail.Id, true, CancellationToken.None));
        Assert.False(mail.IsRead);
    }

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
