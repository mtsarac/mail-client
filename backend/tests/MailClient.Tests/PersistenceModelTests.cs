using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Tests;

public sealed class PersistenceModelTests
{
    [Fact]
    public void Model_HasNoUserEntityAndScopesSendToAccount()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase("model").Options);
        var names = db.Model.GetEntityTypes().Select(x => x.ClrType.Name).ToArray();
        Assert.DoesNotContain("User", names);
        var send = db.Model.FindEntityType(typeof(Domain.Entities.SendOperation))!;
        Assert.Contains(send.GetIndexes(), x => x.Properties.Select(p => p.Name).SequenceEqual(["MailAccountId", "IdempotencyKey"]) && x.IsUnique);
    }
}
