using MailClient.Application.Sync;
using MailClient.Infrastructure.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MailClient.Infrastructure.Services;

public sealed class MailSyncService(
    IServiceScopeFactory scopes,
    ILogger<MailSyncService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var pollIntervalSeconds = 30;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var operationSettings = scope.ServiceProvider.GetRequiredService<RuntimeOperationSettings>();
                var snapshot = await operationSettings.GetAsync(stoppingToken);
                pollIntervalSeconds = snapshot.Settings.Sync.PollIntervalSeconds;
                if (snapshot.Settings.Sync.Enabled)
                {
                    var sync = scope.ServiceProvider.GetRequiredService<MailFolderSyncService>();
                    await sync.SyncAllAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Mail sync cycle failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollIntervalSeconds), stoppingToken);
        }
    }
}
