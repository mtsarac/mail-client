using MailClient.Application.Validation;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Push;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MailClient.Infrastructure.Tests.Services;

public sealed class DeviceTokenServiceTests
{
    [Fact]
    public async Task Register_NewToken_CreatesRow()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        var userId = Guid.NewGuid();

        var result = await service.RegisterAsync(userId, "fcm-token-1", "android", CancellationToken.None);

        Assert.Equal("android", result.Platform);
        Assert.NotNull(result.LastSeenAt);
        var stored = await db.DeviceTokens.SingleAsync();
        Assert.Equal(userId, stored.UserId);
        Assert.Equal("fcm-token-1", stored.Token);
    }

    [Fact]
    public async Task Register_PlatformNormalizedToLowercase()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var result = await service.RegisterAsync(Guid.NewGuid(), "fcm-token-1", "Android", CancellationToken.None);

        Assert.Equal("android", result.Platform);
        Assert.Equal("android", (await db.DeviceTokens.SingleAsync()).Platform);
    }

    [Theory]
    [InlineData("windows")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("android " + "x")]
    public async Task Register_UnknownPlatform_Rejected(string platform)
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            service.RegisterAsync(Guid.NewGuid(), "fcm-token-1", platform, CancellationToken.None));
        Assert.Empty(await db.DeviceTokens.ToListAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Register_BlankToken_Rejected(string token)
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            service.RegisterAsync(Guid.NewGuid(), token, "ios", CancellationToken.None));
    }

    [Fact]
    public async Task Register_TooLongToken_Rejected()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            service.RegisterAsync(Guid.NewGuid(), new string('t', 501), "ios", CancellationToken.None));
    }

    [Fact]
    public async Task Register_SameUserSameToken_UpdatesWithoutDuplicate()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        var userId = Guid.NewGuid();
        var first = await service.RegisterAsync(userId, "fcm-token-1", "android", CancellationToken.None);

        var second = await service.RegisterAsync(userId, "fcm-token-1", "ios", CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("ios", second.Platform);
        Assert.Equal(first.RegisteredAt, second.RegisteredAt);
        Assert.Single(await db.DeviceTokens.ToListAsync());
    }

    [Fact]
    public async Task Register_OtherUsersToken_ReassignedSilently()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        await service.RegisterAsync(userA, "fcm-token-1", "android", CancellationToken.None);

        var result = await service.RegisterAsync(userB, "fcm-token-1", "ios", CancellationToken.None);

        var stored = await db.DeviceTokens.SingleAsync();
        Assert.Equal(userB, stored.UserId);
        Assert.Equal("ios", stored.Platform);
        Assert.Equal(result.Id, stored.Id);
        Assert.Single(await db.DeviceTokens.ToListAsync());
    }

    [Fact]
    public async Task Delete_OwnToken_Removes()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        var userId = Guid.NewGuid();
        var device = await service.RegisterAsync(userId, "fcm-token-1", "android", CancellationToken.None);

        Assert.True(await service.DeleteAsync(userId, device.Id, CancellationToken.None));
        Assert.Empty(await db.DeviceTokens.ToListAsync());
    }

    [Fact]
    public async Task Delete_OtherUsersToken_ReturnsFalseWithoutDelete()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        var device = await service.RegisterAsync(Guid.NewGuid(), "fcm-token-1", "android", CancellationToken.None);

        Assert.False(await service.DeleteAsync(Guid.NewGuid(), device.Id, CancellationToken.None));
        Assert.Single(await db.DeviceTokens.ToListAsync());
    }

    [Fact]
    public async Task Delete_UnknownId_ReturnsFalse()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        Assert.False(await service.DeleteAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None));
    }

    private static DeviceTokenService CreateService(AppDbContext db) =>
        new(db, NullLogger<DeviceTokenService>.Instance);

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
}
