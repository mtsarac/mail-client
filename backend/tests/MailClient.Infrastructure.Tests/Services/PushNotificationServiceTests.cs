using MailClient.Application.Interfaces;
using MailClient.Domain.Entities;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Push;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MailClient.Infrastructure.Tests.Services;

public sealed class PushNotificationServiceTests
{
    [Fact]
    public async Task NotifyNewMail_MapsPayloadAndNotifiesAllUserTokens()
    {
        var userId = Guid.NewGuid();
        var mailId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        await using var provider = BuildProvider();
        await SeedTokenAsync(provider, userId, "token-a", "android");
        await SeedTokenAsync(provider, userId, "token-b", "ios");
        var gateway = provider.GetRequiredService<FakeFirebaseGateway>();
        var service = provider.GetRequiredService<IPushNotificationService>();

        await service.NotifyNewMailAsync(
            new NewMailNotification(userId, mailId, accountId, folderId, "Sender Name", "Subject line"),
            CancellationToken.None);

        var call = Assert.Single(gateway.Calls);
        Assert.Equal(["token-a", "token-b"], call.Recipients.Select(r => r.PushToken).OrderBy(t => t).ToArray());
        Assert.Equal("Sender Name", call.Title);
        Assert.Equal("Subject line", call.Body);
        Assert.Equal("new_mail", call.Data["type"]);
        Assert.Equal(mailId.ToString(), call.Data["mailId"]);
        Assert.Equal(accountId.ToString(), call.Data["accountId"]);
        Assert.Equal(folderId.ToString(), call.Data["folderId"]);
        Assert.DoesNotContain("body", call.Data.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NotifyNewMail_FallsBackToAddressWhenDisplayNameMissing()
    {
        var userId = Guid.NewGuid();
        await using var provider = BuildProvider();
        await SeedTokenAsync(provider, userId, "token-a", "android");
        var gateway = provider.GetRequiredService<FakeFirebaseGateway>();

        // Sender already resolved by the caller (sync service); empty display
        // name arrives as the raw address.
        await provider.GetRequiredService<IPushNotificationService>().NotifyNewMailAsync(
            new NewMailNotification(userId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "sender@example.test", "Hi"),
            CancellationToken.None);

        Assert.Equal("sender@example.test", Assert.Single(gateway.Calls).Title);
    }

    [Fact]
    public async Task NotifyNewMail_RemovesInvalidKeepsTransient()
    {
        var userId = Guid.NewGuid();
        await using var provider = BuildProvider();
        var kept = await SeedTokenAsync(provider, userId, "token-ok", "android");
        var invalid = await SeedTokenAsync(provider, userId, "token-bad", "android");
        var flaky = await SeedTokenAsync(provider, userId, "token-flaky", "ios");
        var gateway = provider.GetRequiredService<FakeFirebaseGateway>();
        gateway.Decide = recipient => recipient.PushToken switch
        {
            "token-bad" => new FirebaseSendResult(recipient.DbId, false, true),
            "token-flaky" => new FirebaseSendResult(recipient.DbId, false, false),
            _ => new FirebaseSendResult(recipient.DbId, true, false)
        };

        await provider.GetRequiredService<IPushNotificationService>().NotifyNewMailAsync(
            new NewMailNotification(userId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "S", "T"),
            CancellationToken.None);

        using var scope = provider.CreateScope();
        var remaining = await scope.ServiceProvider.GetRequiredService<AppDbContext>().DeviceTokens
            .Select(t => t.Id).ToListAsync();
        Assert.DoesNotContain(invalid, remaining);
        Assert.Contains(kept, remaining);
        Assert.Contains(flaky, remaining);
    }

    [Fact]
    public async Task NotifyNewMail_NoTokens_DoesNotCallGateway()
    {
        await using var provider = BuildProvider();
        var gateway = provider.GetRequiredService<FakeFirebaseGateway>();

        await provider.GetRequiredService<IPushNotificationService>().NotifyNewMailAsync(
            new NewMailNotification(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "S", "T"),
            CancellationToken.None);

        Assert.Empty(gateway.Calls);
    }

    [Fact]
    public async Task NotifyNewMail_NeverNotifiesOtherUsersTokens()
    {
        var userId = Guid.NewGuid();
        await using var provider = BuildProvider();
        await SeedTokenAsync(provider, Guid.NewGuid(), "token-other", "android");
        await SeedTokenAsync(provider, userId, "token-mine", "android");
        var gateway = provider.GetRequiredService<FakeFirebaseGateway>();

        await provider.GetRequiredService<IPushNotificationService>().NotifyNewMailAsync(
            new NewMailNotification(userId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "S", "T"),
            CancellationToken.None);

        Assert.Equal(["token-mine"], Assert.Single(gateway.Calls).Recipients.Select(r => r.PushToken).ToArray());
    }

    [Fact]
    public async Task NotifyNewMail_GatewayFailure_DoesNotThrow()
    {
        var userId = Guid.NewGuid();
        await using var provider = BuildProvider();
        await SeedTokenAsync(provider, userId, "token-a", "android");
        provider.GetRequiredService<FakeFirebaseGateway>().Throw = new InvalidOperationException("firebase down");

        await provider.GetRequiredService<IPushNotificationService>().NotifyNewMailAsync(
            new NewMailNotification(userId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "S", "T"),
            CancellationToken.None);

        using var scope = provider.CreateScope();
        Assert.Single(await scope.ServiceProvider.GetRequiredService<AppDbContext>().DeviceTokens.ToListAsync());
    }

    [Fact]
    public async Task NoOpPush_DoesNothing()
    {
        var service = new NoOpPushNotificationService();
        await service.NotifyNewMailAsync(
            new NewMailNotification(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "S", "T"),
            CancellationToken.None);
    }

    private static ServiceProvider BuildProvider()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(dbName));
        services.AddSingleton<FakeFirebaseGateway>();
        services.AddSingleton<IFirebaseGateway>(provider => provider.GetRequiredService<FakeFirebaseGateway>());
        services.AddScoped<IPushNotificationService, FirebasePushNotificationService>();
        services.AddSingleton<ILogger<FirebasePushNotificationService>>(NullLogger<FirebasePushNotificationService>.Instance);
        return services.BuildServiceProvider();
    }

    private static async Task<Guid> SeedTokenAsync(ServiceProvider provider, Guid userId, string token, string platform)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (!await db.Users.AnyAsync(u => u.Id == userId))
        {
            db.Users.Add(new User
            {
                Id = userId,
                Email = $"push-{userId:N}@example.test",
                PasswordHash = "x",
                DisplayName = "Push"
            });
        }
        var device = new DeviceToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Token = token,
            Platform = platform,
            RegisteredAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow
        };
        db.DeviceTokens.Add(device);
        await db.SaveChangesAsync();
        return device.Id;
    }

    private sealed class FakeFirebaseGateway : IFirebaseGateway
    {
        public List<FakeCall> Calls { get; } = [];
        public Func<FirebaseRecipient, FirebaseSendResult>? Decide { get; set; }
        public Exception? Throw { get; set; }

        public Task<IReadOnlyList<FirebaseSendResult>> SendNewMailAsync(
            IReadOnlyList<FirebaseRecipient> recipients,
            string title,
            string body,
            IReadOnlyDictionary<string, string> data,
            CancellationToken cancellationToken)
        {
            if (Throw is not null)
                throw Throw;
            Calls.Add(new FakeCall(
                recipients.ToList(), title, body, new Dictionary<string, string>(data)));
            return Task.FromResult<IReadOnlyList<FirebaseSendResult>>(
                recipients.Select(r => Decide?.Invoke(r) ?? new FirebaseSendResult(r.DbId, true, false)).ToList());
        }

        public sealed record FakeCall(
            IReadOnlyList<FirebaseRecipient> Recipients,
            string Title,
            string Body,
            IReadOnlyDictionary<string, string> Data);
    }
}
