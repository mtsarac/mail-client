using System.Collections.Concurrent;
using System.Threading.Channels;
using MailClient.Application.Sync;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MailClient.Infrastructure.Mail;

public sealed class MailOperationsService(AppDbContext db)
{
    public Task<bool> OwnsFolderAsync(Guid accountId, Guid folderId, CancellationToken cancellationToken) =>
        db.MailFolders.AnyAsync(x => x.Id == folderId && x.MailAccountId == accountId, cancellationToken);

    public Task<MailFolderSyncAvailability?> GetFolderSyncAvailabilityAsync(Guid accountId, Guid folderId, CancellationToken cancellationToken) =>
        db.MailFolders
            .Where(x => x.Id == folderId && x.MailAccountId == accountId)
            .Select(x => new MailFolderSyncAvailability(x.IsAvailable))
            .SingleOrDefaultAsync(cancellationToken);
}

public sealed record MailFolderSyncAvailability(bool IsAvailable);

public sealed class InitialSyncQueue
{
    private readonly Channel<SyncRequest> _channel = Channel.CreateUnbounded<SyncRequest>();
    private readonly ConcurrentDictionary<SyncRequest, byte> _pending = new();

    public async ValueTask EnqueueAsync(SyncRequest request, CancellationToken cancellationToken)
    {
        if (!_pending.TryAdd(request, 0)) return;
        await _channel.Writer.WriteAsync(request, cancellationToken);
    }

    public async IAsyncEnumerable<SyncRequest> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var request in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            _pending.TryRemove(request, out _);
            yield return request;
        }
    }
}

public sealed class InitialSyncWorker(
    InitialSyncQueue queue,
    IServiceScopeFactory scopes,
    ILogger<InitialSyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
