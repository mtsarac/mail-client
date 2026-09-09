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

public class AuthenticationServiceTests
{
    [Fact]
    public async Task RegisterAsync_RejectsNullRequestWithoutThrowing()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var result = await service.RegisterAsync(null!, "Open", CancellationToken.None);

        Assert.Equal(ServiceOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public async Task RegisterAsync_RejectsDuplicateEmail()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        var first = await service.RegisterAsync(new RegisterUserRequest("user@example.com", "long-enough", "User"), "Open", CancellationToken.None);

        var second = await service.RegisterAsync(new RegisterUserRequest("USER@example.com", "long-enough", "User"), "Open", CancellationToken.None);

        Assert.Equal(ServiceOutcome.Ok, first.Outcome);
        Assert.Equal(ServiceOutcome.Conflict, second.Outcome);
    }

    [Fact]
    public async Task LoginAsync_RejectsWrongPassword()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        await service.RegisterAsync(new RegisterUserRequest("user@example.com", "correct-password", "User"), "Open", CancellationToken.None);

        var result = await service.LoginAsync(new LoginUserRequest("user@example.com", "wrong-password"), CancellationToken.None);

        Assert.Equal(ServiceOutcome.Unauthorized, result.Outcome);
    }

    [Fact]
    public async Task LoginAsync_RejectsNonActiveUser()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        await service.RegisterAsync(new RegisterUserRequest("user@example.com", "correct-password", "User"), "ApprovalRequired", CancellationToken.None);

        var result = await service.LoginAsync(new LoginUserRequest("user@example.com", "correct-password"), CancellationToken.None);

        Assert.Equal(ServiceOutcome.Forbidden, result.Outcome);
    }

    [Fact]
    public async Task LoginAsync_ReturnsCurrentTokenVersion()
    {
        await using var db = CreateDb();
        var service = CreateService(db);
        await service.RegisterAsync(new RegisterUserRequest("user@example.com", "correct-password", "User"), "Open", CancellationToken.None);

        var result = await service.LoginAsync(new LoginUserRequest("user@example.com", "correct-password"), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.Value!.TokenVersion);
    }

    private static AuthenticationService CreateService(AppDbContext db) => new(
        db, new PasswordHasher<User>(), NullLogger<AuthenticationService>.Instance);

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options);
}
