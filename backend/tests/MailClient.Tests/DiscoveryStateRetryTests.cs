using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MailClient.Application.Discovery;
using MailClient.Application.Mail;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Discovery;
using MailClient.Infrastructure.Mail;
using MailClient.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MailClient.Tests;

public sealed class ToggleMailValidator : IMailConnectionValidator, IMailServerCandidateValidator
{
    public volatile bool AcceptCredentials;
    public Task<bool> ValidateAsync(MailServerCandidate candidate, CancellationToken cancellationToken) => Task.FromResult(true);
    public Task<bool> ValidateCandidateAsync(MailServerCandidate candidate, CancellationToken cancellationToken) => Task.FromResult(true);
    public Task ValidateCredentialsAsync(MailServerCandidate candidate, string username, string password, CancellationToken cancellationToken) =>
        AcceptCredentials ? Task.CompletedTask : throw new MailConnectionException(MailConnectionFailure.Authentication, "rejected");
    public Task ValidateOAuthCredentialsAsync(MailServerCandidate candidate, string username, string accessToken, CancellationToken cancellationToken) =>
        AcceptCredentials ? Task.CompletedTask : throw new MailConnectionException(MailConnectionFailure.Authentication, "rejected");
}

public sealed class DiscoveryRetryApiFactory : WebApplicationFactory<Program>
{
    public ToggleMailValidator Validator { get; } = new();
    public string DbName { get; } = Guid.NewGuid().ToString("N");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>)).ToList())
                services.Remove(descriptor);
            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IMailConnectionValidator) || d.ServiceType == typeof(IMailServerCandidateValidator)).ToList())
                services.Remove(descriptor);
            var internalProvider = new ServiceCollection().AddEntityFrameworkInMemoryDatabase().BuildServiceProvider();
            services.AddDbContext<AppDbContext>(options => options
                .UseInMemoryDatabase(DbName)
                .UseInternalServiceProvider(internalProvider));
            services.AddSingleton<IMailConnectionValidator>(Validator);
            services.AddSingleton<IMailServerCandidateValidator>(sp => (ToggleMailValidator)sp.GetRequiredService<IMailConnectionValidator>());
        });
    }
}

public sealed class DiscoveryStateStoreTests
{
    private static readonly MailServerCandidate Candidate = new(
        MailProvider.Custom,
        new MailEndpoint("imap.test.invalid", 993, MailSecurity.SslOnConnect),
        new MailEndpoint("smtp.test.invalid", 465, MailSecurity.SslOnConnect),
        [AuthenticationMethod.Password],
        DiscoverySource.Heuristic);

    [Fact]
    public void Get_KeepsStateUntilConsumed()
    {
        var store = new DiscoveryStateStore();
        var id = store.Store("person@example.test", Candidate, TimeSpan.FromMinutes(10));

        Assert.NotNull(store.Get(id));
        Assert.NotNull(store.Get(id));
    }

    [Fact]
    public void Consume_RemovesStateAndIsIdempotent()
    {
        var store = new DiscoveryStateStore();
        var id = store.Store("person@example.test", Candidate, TimeSpan.FromMinutes(10));

        store.Consume(id);
        store.Consume(id);

        Assert.Null(store.Get(id));
    }

    [Fact]
    public void ExpiredState_IsNotReturned()
    {
        var store = new DiscoveryStateStore();
        var id = store.Store("person@example.test", Candidate, TimeSpan.FromMinutes(-1));

        Assert.Null(store.Get(id));
    }

    [Fact]
    public void Store_UsesUnpredictableIdentifier()
    {
        var store = new DiscoveryStateStore();
        var first = store.Store("a@example.test", Candidate, TimeSpan.FromMinutes(10));
        var second = store.Store("b@example.test", Candidate, TimeSpan.FromMinutes(10));

        Assert.NotEqual(first, second);
        Assert.Equal(64, first.Length);
    }
}

public sealed class DiscoveryRetryApiTests(DiscoveryRetryApiFactory factory) : IClassFixture<DiscoveryRetryApiFactory>
{
    private readonly DiscoveryRetryApiFactory _factory = factory;

    [Fact]
    public async Task FailedAuthentication_KeepsDiscoveryStateUsable()
    {
        var client = _factory.CreateClient();
        var discoveryId = SeedDiscovery();

        var first = await ConnectAsync(client, discoveryId);
        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        Assert.Equal("mail_authentication_failed", await CodeAsync(first));

        var second = await ConnectAsync(client, discoveryId);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        Assert.Equal("mail_authentication_failed", await CodeAsync(second));
    }

    [Fact]
    public async Task SuccessfulConnection_ConsumesDiscoveryState()
    {
        var client = _factory.CreateClient();
        var discoveryId = SeedDiscovery();
        _factory.Validator.AcceptCredentials = true;

        var connected = await ConnectAsync(client, discoveryId);
        Assert.Equal(HttpStatusCode.OK, connected.StatusCode);

        var reused = await ConnectAsync(client, discoveryId);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
        Assert.Equal("discovery_expired", await CodeAsync(reused));
    }

    private string SeedDiscovery()
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<DiscoveryStateStore>();
        return store.Store(
            "person@example.test",
            new MailServerCandidate(
                MailProvider.Custom,
                new MailEndpoint("imap.test.invalid", 993, MailSecurity.SslOnConnect),
                new MailEndpoint("smtp.test.invalid", 465, MailSecurity.SslOnConnect),
                [AuthenticationMethod.Password],
                DiscoverySource.Heuristic),
            TimeSpan.FromMinutes(10));
    }

    private static Task<HttpResponseMessage> ConnectAsync(HttpClient client, string discoveryId) =>
        client.PostAsJsonAsync("/api/accounts/connect", new
        {
            discoveryId,
            authentication = new { type = "Password", password = "WrongPassword123!" }
        });

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonDocument>();
        return body!.RootElement.GetProperty("code").GetString();
    }
}
