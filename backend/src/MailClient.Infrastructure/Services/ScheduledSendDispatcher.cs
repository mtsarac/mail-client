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
/// Polls for due <see cref="ScheduledSend"/> rows and dispatches them through the same
/// <see cref="MailSendService"/> pipeline used by an immediate send. A <see cref="PostgresSyncLockProvider"/>
/// lock held per account for <see cref="SyncLockPurpose.ScheduledSend"/> keeps two running instances from
/// dispatching the same row twice; the row's status is re-checked after the lock is acquired as a
/// defense against a row that was already claimed by another poll pass.
/// </summary>
public sealed class ScheduledSendDispatcher(
    IServiceScopeFactory scopes,
    ILogger<ScheduledSendDispatcher> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

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
            .Where(x => x.Status == ScheduledSendStatus.Pending && x.SendAtUtc <= now)
            .OrderBy(x => x.SendAtUtc)
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
        var storage = provider.GetRequiredService<IFileStorage>();
        var sender = provider.GetRequiredService<MailSendService>();
        var logger = provider.GetRequiredService<ILogger<ScheduledSendDispatcher>>();

        var entity = await db.ScheduledSends.Include(x => x.Attachments)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null || entity.Status != ScheduledSendStatus.Pending || entity.SendAtUtc > DateTime.UtcNow)
            return;

        try
        {
            // MailSendService.SendAsync disposes every attachment stream once the send completes.
            var attachments = new List<SendMailAttachment>(entity.Attachments.Count);
            foreach (var attachment in entity.Attachments)
                attachments.Add(new SendMailAttachment(
                    attachment.FileName, attachment.ContentType,
                    await storage.OpenReadAsync(attachment.StoragePath, cancellationToken)));

            var command = new SendMailCommand(
                entity.MailAccountId,
                ScheduledSendService.Deserialize(entity.ToAddressesJson),
                ScheduledSendService.Deserialize(entity.CcAddressesJson),
                ScheduledSendService.Deserialize(entity.BccAddressesJson),
                entity.Subject,
                entity.BodyHtml,
                entity.BodyText,
                attachments,
                entity.ReplySourceMailId)
            {
                IdempotencyKey = entity.IdempotencyKey
            };

            var result = await sender.SendAsync(entity.MailAccountId, command, null, cancellationToken);
            entity.Status = result.Sent ? ScheduledSendStatus.Sent : ScheduledSendStatus.Failed;
            entity.SentMailId = result.MailId;
            entity.FailureReason = result.Sent ? null : (result.Warning ?? "The message could not be sent.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Scheduled send {Id} could not be dispatched.", id);
            entity.Status = ScheduledSendStatus.Failed;
            entity.FailureReason = ex is InvalidOperationException ? ex.Message : "The message could not be sent.";
        }

        await db.SaveChangesAsync(cancellationToken);

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
}
