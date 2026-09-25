using MailClient.Application.Mail;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MailClient.Infrastructure.Services;

public sealed class SnoozeWakeupService(
    IServiceScopeFactory scopes,
    ILogger<SnoozeWakeupService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private const int BatchSize = 100;

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
                logger.LogError(ex, "Snooze wake-up pass failed.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    internal static async Task ProcessDueAsync(IServiceProvider provider, CancellationToken cancellationToken)
    {
        var db = provider.GetRequiredService<AppDbContext>();
        var push = provider.GetRequiredService<IPushNotificationService>();
        var logger = provider.GetRequiredService<ILogger<SnoozeWakeupService>>();
        var now = DateTime.UtcNow;
        var due = await db.MailSnoozes.AsNoTracking()
            .Where(x => x.UntilUtc <= now)
            .OrderBy(x => x.UntilUtc)
            .Take(BatchSize)
            .Select(x => new { x.Id, x.MailAccountId, x.MailId, x.UntilUtc })
            .ToListAsync(cancellationToken);

        foreach (var snooze in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await ClaimAsync(db, snooze.Id, snooze.UntilUtc, cancellationToken))
                continue;
            var mail = await db.Mails.AsNoTracking()
                .Where(x => x.Id == snooze.MailId && x.MailAccountId == snooze.MailAccountId)
                .Select(x => new { x.Id, x.ConversationId, x.MailFolderId, x.MailFolder!.FolderType, x.FromAddress, x.FromDisplayName, x.Subject, x.BodyText })
                .SingleOrDefaultAsync(cancellationToken);
            if (mail is null || mail.FolderType is MailFolderType.Trash or MailFolderType.Junk)
                continue;
            try
            {
                await push.NotifyAsync(
                    new PushEvent(
                        PushEventType.SnoozeExpired,
                        snooze.MailAccountId,
                        mail.Id,
                        mail.ConversationId,
                        mail.MailFolderId,
                        SenderPreview: string.IsNullOrWhiteSpace(mail.FromDisplayName) ? mail.FromAddress : mail.FromDisplayName,
                        SubjectPreview: mail.Subject,
                        BodyPreview: NewMailNotifier.Preview(mail.BodyText)),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Snooze wake-up push failed. The snooze has already ended.");
            }
        }
    }

    internal static async Task<bool> ClaimAsync(AppDbContext db, Guid id, DateTime untilUtc, CancellationToken cancellationToken)
    {
        var claim = db.MailSnoozes.Where(x => x.Id == id && x.UntilUtc == untilUtc);
        if (db.Database.IsRelational())
            return await claim.ExecuteDeleteAsync(cancellationToken) == 1;
        var row = await claim.SingleOrDefaultAsync(cancellationToken);
        if (row is null)
            return false;
        db.MailSnoozes.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
