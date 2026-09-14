using System.Security.Cryptography;
using System.Text;
using MailClient.Domain.Entities;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MailClient.Infrastructure.Mail;

public sealed record SendMailCommand(string To, string Subject, string? BodyText, string? BodyHtml, string IdempotencyKey);

public sealed class MailOperationsService(AppDbContext db)
{
    public async Task<bool> SyncFolderAsync(Guid accountId, Guid folderId, CancellationToken cancellationToken) =>
        await db.MailFolders.AnyAsync(x => x.Id == folderId && x.MailAccountId == accountId, cancellationToken);

    public async Task<SendOperation> ClaimSendAsync(Guid accountId, SendMailCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey)) throw new InvalidOperationException("idempotency_key_required");
        if (command.To.Contains('\r') || command.To.Contains('\n') || command.Subject.Contains('\r') || command.Subject.Contains('\n')) throw new InvalidOperationException("invalid_mail_header");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{command.To}\n{command.Subject}\n{command.BodyText}\n{command.BodyHtml}")));
        var existing = await db.SendOperations.SingleOrDefaultAsync(x => x.MailAccountId == accountId && x.IdempotencyKey == command.IdempotencyKey, cancellationToken);
        if (existing is not null)
        {
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(existing.Fingerprint), Encoding.ASCII.GetBytes(fingerprint))) throw new InvalidOperationException("idempotency_conflict");
            return existing;
        }
        var operation = new SendOperation { Id = Guid.NewGuid(), MailAccountId = accountId, IdempotencyKey = command.IdempotencyKey, Fingerprint = fingerprint, Status = Domain.Enums.SendOperationStatus.InProgress, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.SendOperations.Add(operation);
        await db.SaveChangesAsync(cancellationToken);
        return operation;
    }
}

public sealed class InitialSyncQueue
{
    private readonly System.Threading.Channels.Channel<Guid> _channel = System.Threading.Channels.Channel.CreateUnbounded<Guid>();
    public ValueTask EnqueueAsync(Guid accountId, CancellationToken cancellationToken) => _channel.Writer.WriteAsync(accountId, cancellationToken);
    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken) => _channel.Reader.ReadAllAsync(cancellationToken);
}

public sealed class InitialSyncWorker(InitialSyncQueue queue, ILogger<InitialSyncWorker> logger) : Microsoft.Extensions.Hosting.BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var accountId in queue.ReadAllAsync(stoppingToken)) logger.LogInformation("Initial synchronization requested for MailAccount {MailAccountId}.", accountId);
    }
}
