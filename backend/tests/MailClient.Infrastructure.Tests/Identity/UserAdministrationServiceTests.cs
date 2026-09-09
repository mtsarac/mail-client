using MailClient.Application;
using MailClient.Application.Interfaces;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Identity;
using MailClient.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MailClient.Infrastructure.Tests.Identity;

public class UserAdministrationServiceTests
{
    [Fact]
    public async Task SetStatusAsync_Disable_BumpsTokenVersion()
    {
        await using var db = CreateDb();
        var admin = CreateService(db);
        var created = await admin.CreateAsync(
            new AdminCreateUserRequest("user@example.com", "long-enough", "User", UserRole.User, UserStatus.Active),
            CancellationToken.None);
        var before = (await db.Users.SingleAsync()).TokenVersion;

        var result = await admin.SetStatusAsync(created.Value!.Id, UserStatus.Disabled, CancellationToken.None);

        Assert.Equal(ServiceOutcome.Ok, result.Outcome);
        Assert.Equal(before + 1, (await db.Users.SingleAsync()).TokenVersion);
    }

    [Fact]
    public async Task ResetPasswordAsync_BumpsTokenVersionAndChangesHash()
    {
        await using var db = CreateDb();
        var admin = CreateService(db);
        var created = await admin.CreateAsync(
            new AdminCreateUserRequest("user@example.com", "long-enough", "User", UserRole.User, UserStatus.Active),
            CancellationToken.None);
        var stored = await db.Users.SingleAsync();
        var beforeVersion = stored.TokenVersion;
        var beforeHash = stored.PasswordHash;

        var result = await admin.ResetPasswordAsync(
            created.Value!.Id, new ResetUserPasswordRequest("brand-new-password"), CancellationToken.None);

        Assert.Equal(ServiceOutcome.Ok, result.Outcome);
        stored = await db.Users.SingleAsync();
        Assert.Equal(beforeVersion + 1, stored.TokenVersion);
        Assert.NotEqual(beforeHash, stored.PasswordHash);
    }

    [Fact]
    public async Task SetStatusAsync_UnknownUser_ReturnsNotFound()
    {
        await using var db = CreateDb();
        var admin = CreateService(db);

        var result = await admin.SetStatusAsync(Guid.NewGuid(), UserStatus.Disabled, CancellationToken.None);

        Assert.Equal(ServiceOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task SessionValidator_RejectsStaleTokenVersion()
    {
        await using var db = CreateDb();
        var admin = CreateService(db);
        var validator = new UserSessionValidator(db);
        var created = await admin.CreateAsync(
            new AdminCreateUserRequest("user@example.com", "long-enough", "User", UserRole.User, UserStatus.Active),
            CancellationToken.None);

        var fresh = await validator.ValidateAsync(created.Value!.Id, 0, CancellationToken.None);
        await admin.SetStatusAsync(created.Value.Id, UserStatus.Disabled, CancellationToken.None);
        var stale = await validator.ValidateAsync(created.Value.Id, 0, CancellationToken.None);

        Assert.NotNull(fresh);
        Assert.Null(stale);
    }

    [Fact]
    public async Task SessionValidator_RejectsDisabledUser()
    {
        await using var db = CreateDb();
        var admin = CreateService(db);
        var validator = new UserSessionValidator(db);
        var created = await admin.CreateAsync(
            new AdminCreateUserRequest("user@example.com", "long-enough", "User", UserRole.User, UserStatus.Disabled),
            CancellationToken.None);

        var session = await validator.ValidateAsync(created.Value!.Id, 0, CancellationToken.None);

        Assert.Null(session);
    }

    [Fact]
    public async Task SessionValidator_RefreshesRoleFromDatabase()
    {
        await using var db = CreateDb();
        var admin = CreateService(db);
        var validator = new UserSessionValidator(db);
        var created = await admin.CreateAsync(
            new AdminCreateUserRequest("user@example.com", "long-enough", "User", UserRole.User, UserStatus.Active),
            CancellationToken.None);
        var stored = await db.Users.SingleAsync();
        stored.Role = UserRole.Admin;
        await db.SaveChangesAsync();

        var session = await validator.ValidateAsync(created.Value!.Id, stored.TokenVersion, CancellationToken.None);

        Assert.NotNull(session);
        Assert.Equal(UserRole.Admin, session.Role);
    }

    private static UserAdministrationService CreateService(AppDbContext db) => new(
        db, new PasswordHasher<User>(), NullLogger<UserAdministrationService>.Instance);

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options);
}
