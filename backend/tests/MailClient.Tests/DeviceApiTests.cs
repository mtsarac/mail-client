using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MailClient.Application.Accounts;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MailClient.Tests;

public sealed class DeviceApiTests(AcceptingApiFactory factory) : IClassFixture<AcceptingApiFactory>
{
    [Fact]
    public async Task Register_SameTokenForTwoAccounts_IsAccountScoped()
    {
        var token = $"tok-{Guid.NewGuid():N}";
        var accountA = await SeedAccountAsync();
        var accountB = await SeedAccountAsync();

        var responseA = await PostDeviceAsync(ClientFor(accountA), new { token, platform = "android" });
        var responseB = await PostDeviceAsync(ClientFor(accountB), new { token, platform = "ios" });

        Assert.Equal(HttpStatusCode.Created, responseA.StatusCode);
        Assert.Equal(HttpStatusCode.Created, responseB.StatusCode);
        var idA = (await responseA.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement.GetProperty("id").GetGuid();
        var idB = (await responseB.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement.GetProperty("id").GetGuid();
        Assert.NotEqual(idA, idB);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(2, await db.DeviceTokens.CountAsync(x => x.Token == token));
    }

    [Fact]
    public async Task Register_SameTokenTwice_IsIdempotentWithoutDuplicates()
    {
        var token = $"tok-{Guid.NewGuid():N}";
        var accountId = await SeedAccountAsync();
        var client = ClientFor(accountId);

        var first = await PostDeviceAsync(client, new { token, platform = "android" });
        var second = await PostDeviceAsync(client, new { token, platform = "android" });

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var firstId = (await first.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement.GetProperty("id").GetGuid();
        var secondId = (await second.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement.GetProperty("id").GetGuid();
        Assert.Equal(firstId, secondId);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.DeviceTokens.CountAsync(
            x => x.MailAccountId == accountId && x.Token == token));
    }

    [Fact]
    public async Task Register_DoesNotReturnPushToken()
    {
        var token = $"secret-tok-{Guid.NewGuid():N}";
        var client = ClientFor(await SeedAccountAsync());

        var response = await PostDeviceAsync(client, new { token, platform = "android", appVersion = "1.0", locale = "en-US" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(token, raw, StringComparison.Ordinal);
        var body = JsonDocument.Parse(raw).RootElement;
        Assert.False(body.TryGetProperty("token", out _));
        Assert.Equal("android", body.GetProperty("platform").GetString());
        Assert.Equal("1.0", body.GetProperty("appVersion").GetString());
        Assert.Equal("en-US", body.GetProperty("locale").GetString());
    }

    [Fact]
    public async Task Register_Again_UpdatesMetadataAndLastSeenAt()
    {
        var token = $"tok-{Guid.NewGuid():N}";
        var accountId = await SeedAccountAsync();
        var client = ClientFor(accountId);

        var first = await PostDeviceAsync(client, new { token, platform = "android", appVersion = "1.0" });
        var second = await PostDeviceAsync(client, new { token, platform = "ios", appVersion = "2.0", locale = "tr-TR" });

        var firstBody = (await first.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement;
        var secondBody = (await second.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement;
        Assert.Equal(JsonValueKind.Null, firstBody.GetProperty("lastSeenAt").ValueKind);
        Assert.Equal("ios", secondBody.GetProperty("platform").GetString());
        Assert.Equal("2.0", secondBody.GetProperty("appVersion").GetString());
        Assert.Equal("tr-TR", secondBody.GetProperty("locale").GetString());
        Assert.Equal(JsonValueKind.String, secondBody.GetProperty("lastSeenAt").ValueKind);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.DeviceTokens.SingleAsync(x => x.MailAccountId == accountId && x.Token == token);
        Assert.Equal("2.0", stored.AppVersion);
        Assert.Equal("tr-TR", stored.Locale);
        Assert.NotNull(stored.LastSeenAt);
    }

    [Fact]
    public async Task Delete_OtherAccountsDevice_ReturnsNotFoundAndKeepsRow()
    {
        var token = $"tok-{Guid.NewGuid():N}";
        var ownerId = await SeedAccountAsync();
        var otherId = await SeedAccountAsync();
        var created = await PostDeviceAsync(ClientFor(ownerId), new { token, platform = "android" });
        var deviceId = (await created.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement.GetProperty("id").GetGuid();

        var forbidden = await ClientFor(otherId).DeleteAsync($"/api/devices/{deviceId}");

        Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.NotNull(await db.DeviceTokens.SingleOrDefaultAsync(x => x.Id == deviceId));

        var ownerDelete = await ClientFor(ownerId).DeleteAsync($"/api/devices/{deviceId}");
        Assert.Equal(HttpStatusCode.NoContent, ownerDelete.StatusCode);
    }

    private HttpClient ClientFor(Guid accountId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", factory.Services.GetRequiredService<IJwtTokenIssuer>().Issue(accountId).Token);
        return client;
    }

    private static Task<HttpResponseMessage> PostDeviceAsync(HttpClient client, object payload) =>
        client.PostAsJsonAsync("/api/devices", payload);

    private async Task<Guid> SeedAccountAsync()
    {
        var accountId = Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.MailAccounts.Add(new MailAccount
        {
            Id = accountId,
            EmailAddress = $"{accountId:N}@example.test",
            NormalizedEmailAddress = $"{accountId:N}@EXAMPLE.TEST",
            Username = "device-test",
            ImapHost = "imap.example.test",
            ImapPort = 993,
            SmtpHost = "smtp.example.test",
            SmtpPort = 587,
            Status = MailAccountStatus.Active
        });
        await db.SaveChangesAsync();
        return accountId;
    }
}
