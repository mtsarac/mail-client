using MailClient.Application.Sync;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MailClient.Infrastructure.Mail;

public sealed class InitialSyncWorker(
    InitialSyncQueue queue,
    IServiceScopeFactory scopes,
    ILogger<InitialSyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetService<AppDbContext>();
            if (db is not null)
            {
                var pending = await db.Mails
                    .Where(mail => mail.ReconciliationState == MailReconciliationState.Pending && mail.ExpectedMailFolderId != null)
                    .Select(mail => new { mail.MailAccountId, FolderId = mail.ExpectedMailFolderId!.Value })
                    .Distinct()
                    .ToListAsync(stoppingToken);
                foreach (var request in pending)
                    await queue.EnqueueAsync(SyncRequest.Folder(request.MailAccountId, request.FolderId), stoppingToken);
            }
        }

        await foreach (var request in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var executor = scope.ServiceProvider.GetRequiredService<ISyncExecutor>();
                if (request.FolderId is { } folderId)
                    await executor.SyncFolderAsync(request.AccountId, folderId, stoppingToken);
                else
                    await executor.SyncAccountAsync(request.AccountId, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Queued mailbox synchronization failed.");
            }
        }
    }
}
