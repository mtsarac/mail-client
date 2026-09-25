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

    [Theory]
    [InlineData(typeof(Domain.Entities.SendOperation), typeof(Domain.Entities.MailAccount), DeleteBehavior.Cascade)]
    [InlineData(typeof(Domain.Entities.DeviceToken), typeof(Domain.Entities.MailAccount), DeleteBehavior.Cascade)]
    [InlineData(typeof(Domain.Entities.SyncSkippedUid), typeof(Domain.Entities.MailFolder), DeleteBehavior.Cascade)]
    [InlineData(typeof(Domain.Entities.Attachment), typeof(Domain.Entities.MailAccount), DeleteBehavior.Cascade)]
    [InlineData(typeof(Domain.Entities.SyncState), typeof(Domain.Entities.MailAccount), DeleteBehavior.Cascade)]
    [InlineData(typeof(Domain.Entities.MailTemplate), typeof(Domain.Entities.MailAccount), DeleteBehavior.Cascade)]
    [InlineData(typeof(Domain.Entities.AuditLog), typeof(Domain.Entities.MailAccount), DeleteBehavior.SetNull)]
    public void AccountOwnedEntities_HaveOwnershipForeignKeys(Type dependent, Type principal, DeleteBehavior expected)
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase("model-fk").Options);
        var entity = db.Model.FindEntityType(dependent)!;

        var foreignKey = entity.GetForeignKeys().SingleOrDefault(candidate => candidate.PrincipalEntityType.ClrType == principal);

        Assert.NotNull(foreignKey);
        Assert.Equal(expected, foreignKey.DeleteBehavior);
    }
}
