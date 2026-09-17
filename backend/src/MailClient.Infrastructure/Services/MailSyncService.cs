using MailClient.Application.Sync;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Runtime;
using MailClient.Infrastructure.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MailClient.Infrastructure.Services;

public sealed class MailSyncService(
    IServiceScopeFactory scopes,
    SyncScheduleQueue queue,
    SyncCoordinator coordinator,
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
                var provider = scope.ServiceProvider;
                var operationSettings = provider.GetRequiredService<RuntimeOperationSettings>();
                var snapshot = await operationSettings.GetAsync(stoppingToken);
                pollIntervalSeconds = snapshot.Settings.Sync.PollIntervalSeconds;
                queue.UpdateCapacity(snapshot.Settings.Sync.QueueCapacity);
                if (snapshot.Settings.Sync.Enabled)
                    await SchedulePeriodicAsync(provider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Mail sync scheduling failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(pollIntervalSeconds), stoppingToken);
        }
    }

    private async Task SchedulePeriodicAsync(IServiceProvider provider, CancellationToken cancellationToken)
    {
        var db = provider.GetRequiredService<AppDbContext>();
        var folders = await db.MailFolders.AsNoTracking()
            .Where(folder => folder.IsSyncEnabled
                && folder.IsAvailable
                && folder.MailAccount!.Status == MailAccountStatus.Active)
            .Select(folder => new { folder.MailAccountId, folder.Id, folder.FolderType })
            .ToListAsync(cancellationToken);
        foreach (var folder in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await coordinator.ScheduleAsync(
                SyncScheduling.ForPeriodicFolder(folder.MailAccountId, folder.Id, folder.FolderType, queue.NextSequence()),
                cancellationToken);
        }
    }
}
