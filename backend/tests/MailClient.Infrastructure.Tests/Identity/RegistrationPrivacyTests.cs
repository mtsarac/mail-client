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

public sealed class RegistrationPrivacyTests
{
    [Fact]
    public async Task Register_ExistingEmail_ReturnsConflictWithoutDetails()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        await service.RegisterAsync(new RegisterUserRequest("user@example.com", "long-enough", "User"), "Open", CancellationToken.None);

        var result = await service.RegisterAsync(new RegisterUserRequest("USER@example.com", "long-enough", "Other"), "Open", CancellationToken.None);

        Assert.Equal(ServiceOutcome.Conflict, result.Outcome);
        Assert.Null(result.Value);
        Assert.Single(await db.Users.ToListAsync());
    }

    [Fact]
    public async Task Register_DisabledMode_RejectsBeforeExistenceCheck()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        await service.RegisterAsync(new RegisterUserRequest("user@example.com", "long-enough", "User"), "Open", CancellationToken.None);

        var existing = await service.RegisterAsync(
            new RegisterUserRequest("user@example.com", "long-enough", "User"), "Disabled", CancellationToken.None);
        var fresh = await service.RegisterAsync(
            new RegisterUserRequest("fresh@example.com", "long-enough", "Fresh"), "Disabled", CancellationToken.None);

        Assert.Equal(ServiceOutcome.Invalid, existing.Outcome);
        Assert.Equal(ServiceOutcome.Invalid, fresh.Outcome);
        Assert.Single(await db.Users.ToListAsync());
    }

    [Fact]
    public async Task Register_DisabledMode_CreatesNothing()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var result = await service.RegisterAsync(
            new RegisterUserRequest("fresh@example.com", "long-enough", "Fresh"), "Disabled", CancellationToken.None);

        Assert.Equal(ServiceOutcome.Invalid, result.Outcome);
        Assert.Empty(await db.Users.ToListAsync());
    }

    [Fact]
    public async Task Register_InvalidInput_StillReported()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var result = await service.RegisterAsync(
            new RegisterUserRequest("not-an-email", "short", ""), "Open", CancellationToken.None);

        Assert.Equal(ServiceOutcome.Invalid, result.Outcome);
        Assert.Contains("email", result.Errors.Keys);
    }

    [Fact]
    public async Task AdminCreate_DuplicateEmail_StaysExplicitConflict()
    {
        await using var db = CreateDb();
        var admin = new UserAdministrationService(
            db, new PasswordHasher<User>(), NullLogger<UserAdministrationService>.Instance);
        var created = await admin.CreateAsync(
            new AdminCreateUserRequest("admin-dup@example.com", "long-enough", "Admin", UserRole.User, UserStatus.Active),
            CancellationToken.None);
        Assert.Equal(ServiceOutcome.Ok, created.Outcome);

        var duplicate = await admin.CreateAsync(
            new AdminCreateUserRequest("ADMIN-DUP@example.com", "long-enough", "Admin", UserRole.User, UserStatus.Active),
            CancellationToken.None);

        Assert.Equal(ServiceOutcome.Conflict, duplicate.Outcome);
    }

    private static AuthenticationService CreateService(AppDbContext db) => new(
        db, new PasswordHasher<User>(), NullLogger<AuthenticationService>.Instance);

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options);
}
