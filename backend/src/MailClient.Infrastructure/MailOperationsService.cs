using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MailClient.Infrastructure.Mail;

public sealed class MailOperationsService(AppDbContext db)
{
    public async Task<bool> SyncFolderAsync(Guid accountId, Guid folderId, CancellationToken cancellationToken) =>
        await db.MailFolders.AnyAsync(x => x.Id == folderId && x.MailAccountId == accountId, cancellationToken);
}

public sealed class InitialSyncQueue
{
    private readonly System.Threading.Channels.Channel<Guid> _channel = System.Threading.Channels.Channel.CreateUnbounded<Guid>();
    public ValueTask EnqueueAsync(Guid accountId, CancellationToken cancellationToken) => _channel.Writer.WriteAsync(accountId, cancellationToken);
    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken) => _channel.Reader.ReadAllAsync(cancellationToken);
}

public sealed class InitialSyncWorker(
    InitialSyncQueue queue,
    IServiceScopeFactory scopes,
    ILogger<InitialSyncWorker> logger) : Microsoft.Extensions.Hosting.BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var accountId in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                logger.LogInformation("A queued mailbox requested initial synchronization.");
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<MailFolderSyncService>().SyncAllAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Queued initial synchronization failed.");
            }
        }
    }
}
