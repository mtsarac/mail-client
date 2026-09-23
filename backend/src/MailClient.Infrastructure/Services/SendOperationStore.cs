using System.Security.Cryptography;
using System.Text;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Email;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MailClient.Infrastructure.Services;

public sealed class SendOperationStore(AppDbContext db, ILogger<SendOperationStore> logger)
{
    internal const int MaxKeyLength = 200;
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    public abstract record Claim;
    public sealed record Proceed(SendOperation Operation) : Claim;
    public sealed record Replay(SendOperation Operation) : Claim;
    public sealed record Denied(string Reason) : Claim;

    public async Task<Claim> ClaimAsync(
        Guid accountId,
        string key,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                return await ClaimOnceAsync(accountId, key, fingerprint, cancellationToken);
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                db.ChangeTracker.Clear();
            }
        }

        return new Denied("Send operation is already in progress.");
    }

    /// <summary>The successfully delivered operation for this key, if any.</summary>
    public Task<SendOperation?> FindCompletedAsync(Guid accountId, string key, CancellationToken cancellationToken) =>
        db.SendOperations.AsNoTracking().SingleOrDefaultAsync(
            operation => operation.MailAccountId == accountId
                && operation.IdempotencyKey == key
                && (operation.Status == SendOperationStatus.Sent || operation.Status == SendOperationStatus.SentWithCopy),
            cancellationToken);

    public Task<bool> TryCompleteAsync(
        Guid operationId,
        SendOperationStatus status,
        bool sentCopySaved,
        string? warning,
        CancellationToken cancellationToken) =>
        TryUpdateAsync(operationId, status, sentCopySaved, warning, cancellationToken);

    public Task<bool> TryFailAsync(
        Guid operationId,
        string? warning,
        CancellationToken cancellationToken) =>
        TryUpdateAsync(operationId, SendOperationStatus.FailedBeforeSend, false, warning, cancellationToken);

    public Task<bool> TryMarkUnknownAsync(
        Guid operationId,
        CancellationToken cancellationToken) =>
        TryUpdateAsync(
            operationId, SendOperationStatus.DeliveryUnknown, false,
            "Delivery status is uncertain; the message may have been sent.", cancellationToken);

    private async Task<Claim> ClaimOnceAsync(
        Guid accountId,
        string key,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        var now = TruncateToMicroseconds(DateTime.UtcNow);
        if (!db.Database.IsRelational())
            return await ClaimCoreAsync(accountId, key, fingerprint, now, cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock({0})", [LockKey(accountId, key)], cancellationToken);
        var claim = await ClaimCoreAsync(accountId, key, fingerprint, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return claim;
    }

    private async Task<Claim> ClaimCoreAsync(
        Guid accountId,
        string key,
        string fingerprint,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var existing = await db.SendOperations.SingleOrDefaultAsync(
            operation => operation.MailAccountId == accountId && operation.IdempotencyKey == key, cancellationToken);
        if (existing is null)
        {
            var operation = new SendOperation
            {
                Id = Guid.NewGuid(),
                MailAccountId = accountId,
                IdempotencyKey = key,
                Fingerprint = fingerprint,
                Status = SendOperationStatus.InProgress,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.SendOperations.Add(operation);
            await db.SaveChangesAsync(cancellationToken);
            return new Proceed(operation);
        }

        if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
            return new Denied("Idempotency key was already used for a different request.");

        switch (existing.Status)
        {
            case SendOperationStatus.Sent:
            case SendOperationStatus.SentWithCopy:
                return new Replay(existing);
            case SendOperationStatus.FailedBeforeSend:
                existing.Status = SendOperationStatus.InProgress;
                existing.UpdatedAt = now;
                await db.SaveChangesAsync(cancellationToken);
                return new Proceed(existing);
            case SendOperationStatus.DeliveryUnknown:
                return new Denied("Send operation delivery status is uncertain; the message may have been sent.");
            default:
                return existing.UpdatedAt > now - StaleAfter
                    ? new Denied("Send operation is already in progress.")
                    : new Denied("Send operation status is uncertain; the message may have been sent.");
        }
    }

    private async Task<bool> TryUpdateAsync(
        Guid operationId,
        SendOperationStatus status,
        bool sentCopySaved,
        string? warning,
        CancellationToken cancellationToken)
    {
        var now = TruncateToMicroseconds(DateTime.UtcNow);
        if (!db.Database.IsRelational())
            return await UpdateCoreAsync(operationId, status, sentCopySaved, warning, now, cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var operation = await db.SendOperations.SingleOrDefaultAsync(
            item => item.Id == operationId, cancellationToken);
        if (operation is not null)
            await db.Database.ExecuteSqlRawAsync(
                "SELECT pg_advisory_xact_lock({0})",
                [LockKey(operation.MailAccountId, operation.IdempotencyKey)],
                cancellationToken);

        var updated = await UpdateCoreAsync(operationId, status, sentCopySaved, warning, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    private async Task<bool> UpdateCoreAsync(
        Guid operationId,
        SendOperationStatus status,
        bool sentCopySaved,
        string? warning,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var operation = await db.SendOperations.SingleOrDefaultAsync(
            item => item.Id == operationId, cancellationToken);
        if (operation is null)
        {
            logger.LogWarning("Send operation {OperationId} no longer exists; skipping status update.", operationId);
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
        IReadOnlyList<(string FileName, string ContentType, long SizeBytes, string ContentHash)> attachments)
    {
        var fields = new List<string>
        {
            accountId.ToString("N"), toAddress, subject, bodyHtml ?? string.Empty, bodyText ?? string.Empty,
            attachments.Count.ToString()
        };
        fields.AddRange(attachments.SelectMany(attachment =>
            new[]
            {
                attachment.FileName, attachment.ContentType,
                attachment.SizeBytes.ToString(), attachment.ContentHash
            }));
        var joined = new StringBuilder();
        foreach (var field in fields)
            joined.Append(field.Length).Append(':').Append(field).Append('\0');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined.ToString())));
    }

    private static DateTime TruncateToMicroseconds(DateTime value) =>
        new(value.Ticks - value.Ticks % 10, value.Kind);

    private static long LockKey(Guid accountId, string key) => BitConverter.ToInt64(
        SHA256.HashData(Encoding.UTF8.GetBytes($"sendop:{accountId:N}:{key}")), 0);
}
