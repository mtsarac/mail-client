using MailClient.Application.Interfaces;
using MailClient.Application.Network;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;

namespace MailClient.Api.Tests;

[CollectionDefinition("integration")]
public sealed class IntegrationCollection : ICollectionFixture<IntegrationFixture>;

public sealed class IntegrationFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("mailclient_tests")
        .Build();

    private ApiTestFactory? _factory;

    public WebApplicationFactory<Program> Factory => _factory
        ?? throw new InvalidOperationException("Fixture is not initialized.");

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _factory = new ApiTestFactory(_postgres.GetConnectionString());
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
            await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private sealed class ApiTestFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Default", connectionString);
            builder.UseSetting("Registration:Mode", "Open");
            builder.UseSetting("Jwt:Key", "integration-tests-key-with-at-least-64-characters-for-hs256!!");
            builder.UseSetting("DataProtection:KeyPath", Path.Combine(Path.GetTempPath(), $"mailclient-tests-{Guid.NewGuid():N}"));
            builder.UseSetting("MailSync:Enabled", "false");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IMailConnectivityTester>();
                services.RemoveAll<IMailFolderExplorer>();
                services.AddSingleton<IMailConnectivityTester, SuccessfulConnectivityTester>();
                services.AddSingleton<IMailFolderExplorer, FixedFolderExplorer>();
            });
        }
    }

    private sealed class SuccessfulConnectivityTester : IMailConnectivityTester
    {
        public Task TestImapAsync(MailServerEndpoint endpoint, string username, string password, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task TestSmtpAsync(MailServerEndpoint endpoint, string username, string password, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class FixedFolderExplorer : IMailFolderExplorer
    {
        public Task<IReadOnlyList<DiscoveredMailFolder>> ExploreAsync(
            MailServerEndpoint endpoint, string username, string password, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DiscoveredMailFolder>>(
            [
                new DiscoveredMailFolder("INBOX", "INBOX", MailFolderType.Inbox, 100, true),
                new DiscoveredMailFolder("Sent", "Sent", MailFolderType.Sent, 7, true)
            ]);
    }
}
