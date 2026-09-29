using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MailClient.Application.Discovery;
using MailClient.Application.Mail;
using MailClient.Application.Runtime;
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
    public int Attempts;
    public MailConnectionFailure Failure = MailConnectionFailure.Authentication;
    public Task<bool> ValidateAsync(MailServerCandidate candidate, CancellationToken cancellationToken) => Task.FromResult(true);
    public Task<bool> ValidateCandidateAsync(MailServerCandidate candidate, CancellationToken cancellationToken) => Task.FromResult(true);
    public Task ValidateCredentialsAsync(MailServerCandidate candidate, string username, string password, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Attempts);
        return AcceptCredentials ? Task.CompletedTask : throw new MailConnectionException(Failure, "rejected");
    }
    public Task ValidateOAuthCredentialsAsync(MailServerCandidate candidate, string username, string accessToken, CancellationToken cancellationToken) =>
        AcceptCredentials ? Task.CompletedTask : throw new MailConnectionException(MailConnectionFailure.Authentication, "rejected");
}

public sealed class DiscoveryRetryApiFactory : WebApplicationFactory<Program>
{
    public ToggleMailValidator Validator { get; } = new();
    public string DbName { get; } = Guid.NewGuid().ToString("N");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");
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

public sealed class AuthenticationAttemptCacheTests
{
    [Fact]
    public async Task Rejection_PreservesOperation_AndDoesNotBlockOtherMailbox()
    {
        using var cache = new MailClient.Infrastructure.Accounts.AuthenticationAttemptCache();
        var validator = new OperationMailValidator();
        var candidate = new MailServerCandidate(
            MailProvider.Custom,
            new MailEndpoint("imap.test.invalid", 993, MailSecurity.SslOnConnect),
            new MailEndpoint("smtp.test.invalid", 465, MailSecurity.SslOnConnect),
            [AuthenticationMethod.Password], DiscoverySource.Heuristic);

        async Task<MailConnectionException> RejectAsync(string email) =>
            await Assert.ThrowsAsync<MailConnectionException>(() =>
                cache.ValidateAsync(validator, candidate, email, email, "wrong-password", new RuntimeAuthenticationSettings(), CancellationToken.None));

        Assert.Equal("ValidateSmtp", (await RejectAsync("first@example.test")).Operation);
        Assert.Equal("ValidateSmtp", (await RejectAsync("first@example.test")).Operation);
        Assert.Equal(1, validator.Attempts);
        Assert.Equal("ValidateSmtp", (await RejectAsync("second@example.test")).Operation);
        Assert.Equal(2, validator.Attempts);
    }

    [Fact]
    public async Task ConcurrentRejection_UsesOneValidatorAttempt_AndCorrectedSecretProceeds()
    {
        using var cache = new MailClient.Infrastructure.Accounts.AuthenticationAttemptCache();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var validator = new DelayedMailValidator(async (_, password) =>
        {
            Interlocked.Increment(ref attempts);
            started.TrySetResult();
            await release.Task;
            if (password == "wrong")
                throw new MailConnectionException(MailConnectionFailure.Authentication, "rejected") { Operation = "ValidateSmtp" };
        });
        var candidate = new MailServerCandidate(MailProvider.Custom,
            new MailEndpoint("imap.test.invalid", 993, MailSecurity.SslOnConnect),
            new MailEndpoint("smtp.test.invalid", 465, MailSecurity.SslOnConnect),
            [AuthenticationMethod.Password], DiscoverySource.Heuristic);
        Task CheckAsync(string password) => cache.ValidateAsync(validator, candidate, "person@example.test", "person@example.test",
            password, new RuntimeAuthenticationSettings(), CancellationToken.None);

        var first = CheckAsync("wrong");
        await started.Task;
        var second = CheckAsync("wrong");
        Assert.Equal(1, Volatile.Read(ref attempts));
        release.SetResult();
        Assert.Equal("ValidateSmtp", (await Assert.ThrowsAsync<MailConnectionException>(() => first)).Operation);
        Assert.Equal("ValidateSmtp", (await Assert.ThrowsAsync<MailConnectionException>(() => second)).Operation);
        Assert.Equal(1, Volatile.Read(ref attempts));
        await CheckAsync("corrected");
        Assert.Equal(2, Volatile.Read(ref attempts));
    }

    [Fact]
    public async Task RepeatedRejection_DoublesConfiguredCooldown_WithoutCachingNetworkFailure()
    {
        var clock = new AdjustableTimeProvider();
        using var cache = new MailClient.Infrastructure.Accounts.AuthenticationAttemptCache(clock);
        var attempts = 0;
        var validator = new DelayedMailValidator((_, _) =>
        {
            var count = Interlocked.Increment(ref attempts);
            throw new MailConnectionException(count == 3 ? MailConnectionFailure.Network : MailConnectionFailure.Authentication, "rejected");
        });
        var settings = new RuntimeAuthenticationSettings { RetryBaseDelaySeconds = 1, RetryMaxDelaySeconds = 4 };
        var candidate = new MailServerCandidate(MailProvider.Custom,
            new MailEndpoint("imap.test.invalid", 993, MailSecurity.SslOnConnect),
            new MailEndpoint("smtp.test.invalid", 465, MailSecurity.SslOnConnect),
            [AuthenticationMethod.Password], DiscoverySource.Heuristic);
        Task RejectAsync() => cache.ValidateAsync(validator, candidate, "person@example.test", "person@example.test",
            "wrong", settings, CancellationToken.None);

        await Assert.ThrowsAsync<MailConnectionException>(RejectAsync);
        await Assert.ThrowsAsync<MailConnectionException>(RejectAsync);
        Assert.Equal(1, attempts);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<MailConnectionException>(RejectAsync);
        Assert.Equal(2, attempts);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<MailConnectionException>(RejectAsync);
        Assert.Equal(2, attempts);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(MailConnectionFailure.Network, (await Assert.ThrowsAsync<MailConnectionException>(RejectAsync)).Failure);
        Assert.Equal(MailConnectionFailure.Authentication, (await Assert.ThrowsAsync<MailConnectionException>(RejectAsync)).Failure);
        Assert.Equal(4, attempts);
    }

    private sealed class AdjustableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class DelayedMailValidator(Func<string, string, Task> validate) : IMailConnectionValidator
    {
        public Task<bool> ValidateCandidateAsync(MailServerCandidate candidate, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task ValidateCredentialsAsync(MailServerCandidate candidate, string username, string password, CancellationToken cancellationToken) =>
            validate(username, password);
        public Task ValidateOAuthCredentialsAsync(MailServerCandidate candidate, string username, string accessToken, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class OperationMailValidator : IMailConnectionValidator
    {
        public int Attempts;
        public Task<bool> ValidateCandidateAsync(MailServerCandidate candidate, CancellationToken cancellationToken) => Task.FromResult(true);


        public Task ValidateCredentialsAsync(MailServerCandidate candidate, string username, string password, CancellationToken cancellationToken)
        {
            Attempts++;
            throw new MailConnectionException(MailConnectionFailure.Authentication, "rejected") { Operation = "ValidateSmtp" };
        }

        public Task ValidateOAuthCredentialsAsync(MailServerCandidate candidate, string username, string accessToken, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

[CollectionDefinition("Discovery retry API", DisableParallelization = true)]
public sealed class DiscoveryRetryCollection;

[Collection("Discovery retry API")]
public sealed class DiscoveryRetryApiTests(DiscoveryRetryApiFactory factory) : IClassFixture<DiscoveryRetryApiFactory>
{
    private readonly DiscoveryRetryApiFactory _factory = factory;

    [Fact]
    public async Task FailedAuthentication_KeepsDiscoveryStateUsable()
    {
        var client = _factory.CreateClient();
        var discoveryId = SeedDiscovery();
        _factory.Validator.AcceptCredentials = false;
        var before = _factory.Validator.Attempts;

        var first = await ConnectAsync(client, discoveryId);
        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        Assert.Equal("mail_authentication_failed", await CodeAsync(first));

        var second = await ConnectAsync(client, discoveryId);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        Assert.Equal("mail_authentication_failed", await CodeAsync(second));
        Assert.Equal(2, _factory.Validator.Attempts - before);
    }

    [Fact]
    public async Task CorrectedPassword_IsNotBlockedByPreviousFailure()
    {
        var client = _factory.CreateClient();
        var discoveryId = SeedDiscovery();
        var failed = await ConnectAsync(client, discoveryId);
        Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        _factory.Validator.AcceptCredentials = true;

        var corrected = await client.PostAsJsonAsync("/api/accounts/connect", new
        {
            discoveryId,
            authentication = new { type = "Password", password = "CorrectedPassword123!" }
        });

        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
    }

    [Fact]
    public async Task NetworkFailure_DoesNotSuppressNextAttempt()
    {
        var client = _factory.CreateClient();
        var discoveryId = SeedDiscovery();
        _factory.Validator.AcceptCredentials = false;
        _factory.Validator.Failure = MailConnectionFailure.Network;
        var before = _factory.Validator.Attempts;
        try
        {
            await ConnectAsync(client, discoveryId);
            await ConnectAsync(client, discoveryId);
            Assert.Equal(2, _factory.Validator.Attempts - before);
        }
        finally
        {
            _factory.Validator.Failure = MailConnectionFailure.Authentication;
        }
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
            $"person-{Guid.NewGuid():N}@example.test",
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
