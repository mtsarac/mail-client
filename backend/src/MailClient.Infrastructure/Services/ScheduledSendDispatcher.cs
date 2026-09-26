using MailClient.Application.Mail;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MailClient.Infrastructure.Services;

/// <summary>
/// Claims due sends in the database before SMTP. A claimed row is delivery-unknown until
/// delivery is proven or a positively pre-delivery failure is recorded. Abandoned claims
/// are never sent again automatically.
/// </summary>
public sealed class ScheduledSendDispatcher(
    IServiceScopeFactory scopes,
    ILogger<ScheduledSendDispatcher> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private const int MaxAttempts = 5;
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await ProcessDueAsync(scope.ServiceProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduled send dispatch pass failed.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    /// <summary>One dispatch pass: finds every due row and processes each in turn. Exposed for tests.</summary>
    internal static async Task ProcessDueAsync(IServiceProvider provider, CancellationToken cancellationToken)
    {
        var db = provider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var due = await db.ScheduledSends.AsNoTracking()
            .Where(x => x.Status == ScheduledSendStatus.Pending && (x.NextAttemptAtUtc ?? x.SendAtUtc) <= now)
            .OrderBy(x => x.NextAttemptAtUtc ?? x.SendAtUtc)
            .Select(x => new { x.Id, x.MailAccountId })
            .ToListAsync(cancellationToken);

        foreach (var row in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DispatchOneAsync(provider, row.Id, row.MailAccountId, cancellationToken);
        }
    }

    private static async Task DispatchOneAsync(IServiceProvider provider, Guid id, Guid accountId, CancellationToken cancellationToken)
    {
        var locks = provider.GetRequiredService<ISyncLockProvider>();
        await using var acquired = await locks.TryAcquireAsync(accountId, SyncLockPurpose.ScheduledSend, cancellationToken);
        if (!acquired.IsAcquired)
            return;

        var db = provider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var eligible = db.ScheduledSends.Where(x => x.Id == id && x.MailAccountId == accountId
            && x.Status == ScheduledSendStatus.Pending && (x.NextAttemptAtUtc ?? x.SendAtUtc) <= now);
        if (db.Database.IsRelational())
        {
            if (await eligible.ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, ScheduledSendStatus.DeliveryUnknown)
                    .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1)
                    .SetProperty(x => x.NextAttemptAtUtc, (DateTime?)null), cancellationToken) != 1)
                return;
        }
        else
        {
            var pending = await eligible.SingleOrDefaultAsync(cancellationToken);
            if (pending is null)
                return;
            pending.Status = ScheduledSendStatus.DeliveryUnknown;
            pending.AttemptCount++;
            pending.NextAttemptAtUtc = null;
            await db.SaveChangesAsync(cancellationToken);
        }

        var entity = await db.ScheduledSends.Include(x => x.Attachments)
            .SingleAsync(x => x.Id == id && x.MailAccountId == accountId, cancellationToken);
        var dispatchKey = ScheduledSendService.DispatchKey(entity);
        var storage = provider.GetRequiredService<IFileStorage>();
        var sender = provider.GetRequiredService<MailSendService>();
        var logger = provider.GetRequiredService<ILogger<ScheduledSendDispatcher>>();
        var attachments = new List<SendMailAttachment>(entity.Attachments.Count);
        var sendStarted = false;
        try
        {
            foreach (var attachment in entity.Attachments)
                attachments.Add(new SendMailAttachment(
                    attachment.FileName, attachment.ContentType,
                    await storage.OpenReadAsync(attachment.StoragePath, cancellationToken)));

            var command = new SendMailCommand(
                entity.MailAccountId,
                ScheduledSendService.Deserialize(entity.ToAddressesJson),
                ScheduledSendService.Deserialize(entity.CcAddressesJson),
                ScheduledSendService.Deserialize(entity.BccAddressesJson),
                entity.Subject, entity.BodyHtml, entity.BodyText, attachments, entity.ReplySourceMailId)
            {
                IdempotencyKey = dispatchKey,
                IdentityId = entity.IdentityId
            };

            sendStarted = true;
            var result = await sender.SendAsync(entity.MailAccountId, command, null, cancellationToken);
            if (result.Sent)
            {
                entity.Status = ScheduledSendStatus.Sent;
                entity.SentMailId = result.MailId;
                entity.FailureReason = null;
            }
            else
            {
                entity.FailureReason = result.Warning ?? "The message could not be sent.";
                ScheduleRetryOrFail(entity, result.PreDeliveryFailure == MailConnectionFailure.Network);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Scheduled send {Id} could not be dispatched.", id);
            var operationStatus = sendStarted
                ? await db.SendOperations.AsNoTracking()
                    .Where(x => x.MailAccountId == accountId && x.IdempotencyKey == dispatchKey)
                    .Select(x => (SendOperationStatus?)x.Status).SingleOrDefaultAsync(CancellationToken.None)
                : null;
            entity.Status = operationStatus switch
            {
                SendOperationStatus.Sent or SendOperationStatus.SentWithCopy => ScheduledSendStatus.Sent,
                SendOperationStatus.FailedBeforeSend => ScheduledSendStatus.Failed,
                _ => sendStarted ? ScheduledSendStatus.DeliveryUnknown : ScheduledSendStatus.Failed
            };
            entity.FailureReason = entity.Status switch
            {
                ScheduledSendStatus.Sent => null,
                ScheduledSendStatus.DeliveryUnknown => "Delivery status is uncertain; the message may have been sent.",
                _ => "The message could not be sent."
            };
        }
        finally
        {
            foreach (var attachment in attachments)
                await attachment.Content.DisposeAsync();
        }

        await db.SaveChangesAsync(cancellationToken);
        if (entity.Status != ScheduledSendStatus.Sent)
            return;

        foreach (var attachment in entity.Attachments)
        {
            try
            {
                await storage.DeleteAsync(attachment.StoragePath, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete staged attachment for dispatched scheduled send {Id}.", id);
            }
        }
    }

    private static void ScheduleRetryOrFail(ScheduledSend entity, bool transient)
    {
        if (!transient || entity.AttemptCount >= MaxAttempts)
        {
            entity.Status = ScheduledSendStatus.Failed;
            return;
        }

        entity.Status = ScheduledSendStatus.Pending;
        entity.NextAttemptAtUtc = DateTime.UtcNow + InitialRetryDelay * (1 << (entity.AttemptCount - 1));
    }
}
