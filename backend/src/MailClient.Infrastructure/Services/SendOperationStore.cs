using System.Security.Cryptography;
using System.Text;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Email;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MailClient.Infrastructure.Services;

// Request-level SMTP deduplication for the send-mail operation.
//
// State machine (scoped per user + idempotency key):
//   (none) --claim--> InProgress --smtp accepted--> Sent --append ok--> SentWithCopy
//   InProgress --pre-send failure--> Failed --retry--> InProgress
//   InProgress older than InProgressTimeout --retry--> InProgress (expiry takeover)
// Same key + different request fingerprint always conflicts (409).
// Failed rows may retry SMTP; Sent/SentWithCopy rows replay the stored result
// and never touch SMTP again. This is request deduplication, not exactly-once
// delivery at the protocol level.
public sealed class SendOperationStore(AppDbContext db, ILogger<SendOperationStore> logger)
{
    internal static readonly TimeSpan InProgressTimeout = TimeSpan.FromMinutes(5);
    internal const int MaxKeyLength = 200;

    public abstract record Claim;
    public sealed record Proceed(SendOperation Operation, DateTime ClaimedAt) : Claim;
    public sealed record Replay(SendOperation Operation) : Claim;
    public sealed record Denied(string Reason) : Claim;

    public async Task<Claim> ClaimAsync(
        Guid userId,
        Guid accountId,
        string key,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                return await ClaimOnceAsync(userId, accountId, key, fingerprint, cancellationToken);
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                // Lost the insert race: the row now exists, fall through and read it.
                db.ChangeTracker.Clear();
            }
        }

        return new Denied("Send operation is already in progress.");
    }

    public Task<bool> TryCompleteAsync(
        Guid operationId,
        DateTime claimedAt,
        SendOperationStatus status,
        bool sentCopySaved,
        string? warning,
        CancellationToken cancellationToken) =>
        TryUpdateAsync(operationId, claimedAt, status, sentCopySaved, warning, cancellationToken);

    public Task<bool> TryFailAsync(
        Guid operationId,
        DateTime claimedAt,
        string? warning,
        CancellationToken cancellationToken) =>
        TryUpdateAsync(operationId, claimedAt, SendOperationStatus.Failed, false, warning, cancellationToken);

    private async Task<Claim> ClaimOnceAsync(
        Guid userId,
        Guid accountId,
        string key,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        var now = TruncateToMicroseconds(DateTime.UtcNow);
        if (!db.Database.IsRelational())
            return await ClaimCoreAsync(userId, accountId, key, fingerprint, now, cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock({0})", [LockKey(userId, key)], cancellationToken);
        var claim = await ClaimCoreAsync(userId, accountId, key, fingerprint, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return claim;
    }

    private async Task<Claim> ClaimCoreAsync(
        Guid userId,
        Guid accountId,
        string key,
        string fingerprint,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var existing = await db.SendOperations.SingleOrDefaultAsync(
            operation => operation.UserId == userId && operation.IdempotencyKey == key, cancellationToken);
        if (existing is null)
        {
            var operation = new SendOperation
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                IdempotencyKey = key,
                AccountId = accountId,
                Fingerprint = fingerprint,
                Status = SendOperationStatus.InProgress,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.SendOperations.Add(operation);
            await db.SaveChangesAsync(cancellationToken);
            return new Proceed(operation, now);
        }

        if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
            return new Denied("Idempotency key was already used for a different request.");

        switch (existing.Status)
        {
            case SendOperationStatus.Sent:
            case SendOperationStatus.SentWithCopy:
                return new Replay(existing);
            case SendOperationStatus.Failed:
                existing.Status = SendOperationStatus.InProgress;
                existing.UpdatedAt = now;
                await db.SaveChangesAsync(cancellationToken);
                return new Proceed(existing, now);
            default:
                if (existing.UpdatedAt > now - InProgressTimeout)
                    return new Denied("Send operation is already in progress.");
                existing.UpdatedAt = now;
                await db.SaveChangesAsync(cancellationToken);
                return new Proceed(existing, now);
        }
    }

    private async Task<bool> TryUpdateAsync(
        Guid operationId,
        DateTime claimedAt,
        SendOperationStatus status,
        bool sentCopySaved,
        string? warning,
        CancellationToken cancellationToken)
    {
        var now = TruncateToMicroseconds(DateTime.UtcNow);
        if (!db.Database.IsRelational())
            return await UpdateCoreAsync(operationId, claimedAt, status, sentCopySaved, warning, now, cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var operation = await db.SendOperations.SingleOrDefaultAsync(
            item => item.Id == operationId, cancellationToken);
        if (operation is not null)
            await db.Database.ExecuteSqlRawAsync(
                "SELECT pg_advisory_xact_lock({0})",
                [LockKey(operation.UserId, operation.IdempotencyKey)],
                cancellationToken);

        var updated = await UpdateCoreAsync(operationId, claimedAt, status, sentCopySaved, warning, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    private async Task<bool> UpdateCoreAsync(
        Guid operationId,
        DateTime claimedAt,
        SendOperationStatus status,
        bool sentCopySaved,
        string? warning,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var operation = await db.SendOperations.SingleOrDefaultAsync(
            item => item.Id == operationId, cancellationToken);
        if (operation is null
            || operation.Status != SendOperationStatus.InProgress
            || operation.UpdatedAt != claimedAt)
        {
            logger.LogWarning("Send operation {OperationId} changed hands; skipping status update.", operationId);
            return false;
        }

        operation.Status = status;
        operation.SentCopySaved = sentCopySaved;
        operation.Warning = warning is null ? null : MailFieldNormalizer.Truncate(warning, 500);
        operation.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    internal static string Fingerprint(
        Guid accountId,
        string toAddress,
        string subject,
        string? bodyHtml,
        string? bodyText,
        IReadOnlyList<(string FileName, string ContentType, long SizeBytes)> attachments)
    {
        var fields = new List<string>
        {
            accountId.ToString("N"), toAddress, subject, bodyHtml ?? string.Empty, bodyText ?? string.Empty,
            attachments.Count.ToString()
        };
        fields.AddRange(attachments.SelectMany(attachment =>
            new[] { attachment.FileName, attachment.ContentType, attachment.SizeBytes.ToString() }));
        var joined = new StringBuilder();
        foreach (var field in fields)
            joined.Append(field.Length).Append(':').Append(field).Append('\0');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined.ToString())));
    }

    private static DateTime TruncateToMicroseconds(DateTime value) =>
        new(value.Ticks - value.Ticks % 10, value.Kind);

    private static long LockKey(Guid userId, string key) => BitConverter.ToInt64(
        SHA256.HashData(Encoding.UTF8.GetBytes($"sendop:{userId:N}:{key}")), 0);
}
