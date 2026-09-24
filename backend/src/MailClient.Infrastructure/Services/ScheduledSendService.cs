using System.Security.Cryptography;
using System.Text.Json;
using MailClient.Application.Mail;
using MailClient.Application.Runtime;
using MailClient.Domain;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Email;
using MailClient.Infrastructure.Observability;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace MailClient.Infrastructure.Services;

public sealed record ScheduledSendCreateResult(Guid Id, DateTime SendAtUtc, ScheduledSendStatus Status);

public sealed record ScheduledSendListItem(
    Guid Id,
    IReadOnlyList<string> To,
    IReadOnlyList<string> Cc,
    IReadOnlyList<string> Bcc,
    string Subject,
    DateTime SendAtUtc,
    ScheduledSendStatus Status,
    DateTime CreatedAtUtc,
    Guid? SentMailId,
    string? FailureReason);

/// <summary>
/// Creates, lists and cancels scheduled sends. The actual delivery at <see cref="ScheduledSend.SendAtUtc"/> is
/// performed by <see cref="ScheduledSendDispatcher"/>, which calls into <see cref="MailSendService"/> — the
/// same send pipeline used by an immediate send — so this service only ever validates and stages a request.
/// </summary>
public sealed class ScheduledSendService(
    AppDbContext db,
    IFileStorage storage,
    RuntimeOperationSettings operationSettings,
    AuditLogger audit,
    ILogger<ScheduledSendService> logger)
{
    public async Task<ScheduledSendCreateResult> CreateAsync(
        Guid accountId,
        SendMailCommand command,
        DateTime sendAtUtc,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        ComposeMailValidator.ValidateIdempotencyKey(command.IdempotencyKey);
        if (sendAtUtc <= DateTime.UtcNow)
            throw new InvalidOperationException("scheduled_send_in_past");

        var limits = (await operationSettings.GetAsync(cancellationToken)).Settings.Limits;
        var recipients = MailRecipientResolver.Parse(command.To, command.Cc, command.Bcc);
        ComposeMailValidator.ValidateBody(command.BodyHtml, command.BodyText, limits);
        ComposeMailValidator.ValidateAttachments(command.Attachments, limits);
        var subject = ComposeMailValidator.ValidateSubject(command.Subject);

        var account = await db.MailAccounts.SingleOrDefaultAsync(
            item => item.Id == accountId && item.Status == MailAccountStatus.Active, cancellationToken)
            ?? throw new InvalidOperationException("mail_account_not_found");

        var hashed = await ComposeMailValidator.HashAttachmentsAsync(command.Attachments, cancellationToken);
        var fingerprintRecipients = string.Join(',', command.To.Concat(command.Cc).Concat(command.Bcc));
        var fingerprint = SendOperationStore.Fingerprint(
            account.Id,
            $"{fingerprintRecipients}|{command.ReplySourceMailId}|{sendAtUtc:O}",
            subject, command.BodyHtml, command.BodyText, hashed);

        var existing = await db.ScheduledSends.SingleOrDefaultAsync(
            x => x.MailAccountId == accountId && x.IdempotencyKey == command.IdempotencyKey, cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException("idempotency_conflict");
            return new(existing.Id, existing.SendAtUtc, existing.Status);
        }

        // Trial-build the MIME message so a malformed request fails now, not silently at dispatch time.
        ComposeMailValidator.RewindAttachments(command.Attachments);
        try
        {
            var to = recipients.To.Select(recipient => new MailboxAddress(recipient.DisplayName, recipient.Address)).ToList();
            var cc = recipients.Cc.Select(recipient => new MailboxAddress(recipient.DisplayName, recipient.Address)).ToList();
            var bcc = recipients.Bcc.Select(recipient => new MailboxAddress(recipient.DisplayName, recipient.Address)).ToList();
            MimeMessageBuilder.Build(account.EmailAddress, account.DisplayName, to, cc, bcc, subject, command.BodyHtml, command.BodyText, command.Attachments, null, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Scheduled send message could not be constructed for account {AccountIdHash}.", ShortHash(accountId));
            throw new InvalidOperationException("message_not_constructible");
        }

        ComposeMailValidator.RewindAttachments(command.Attachments);
        var scheduledSendId = Guid.NewGuid();
        var attachmentRows = new List<ScheduledSendAttachment>();
        foreach (var attachment in command.Attachments)
        {
            var stored = await storage.SaveAsync(
                accountId, scheduledSendId, Guid.NewGuid(),
                (stream, ct) => attachment.Content.CopyToAsync(stream, ct),
                limits.MaxAttachmentBytes, cancellationToken);
            attachmentRows.Add(new ScheduledSendAttachment
            {
                Id = Guid.NewGuid(),
                ScheduledSendId = scheduledSendId,
                FileName = MailFieldNormalizer.FileName(attachment.FileName),
                ContentType = MailFieldNormalizer.ContentType(attachment.ContentType),
                StoragePath = stored.RelativePath,
                SizeBytes = stored.SizeBytes
            });
        }

        var entity = new ScheduledSend
        {
            Id = scheduledSendId,
            MailAccountId = accountId,
            ToAddressesJson = JsonSerializer.Serialize(command.To),
            CcAddressesJson = JsonSerializer.Serialize(command.Cc),
            BccAddressesJson = JsonSerializer.Serialize(command.Bcc),
            Subject = subject,
            BodyHtml = command.BodyHtml,
            BodyText = command.BodyText,
            SendAtUtc = sendAtUtc,
            Status = ScheduledSendStatus.Pending,
            CreatedAtUtc = DateTime.UtcNow,
            IdempotencyKey = command.IdempotencyKey,
            Fingerprint = fingerprint,
            ReplySourceMailId = command.ReplySourceMailId,
            Attachments = attachmentRows
        };
        db.ScheduledSends.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(accountId, AuditActions.ScheduledSendCreated, "ScheduledSend", entity.Id.ToString(), null, correlationId, cancellationToken);
        return new(entity.Id, entity.SendAtUtc, entity.Status);
    }

    public async Task<IReadOnlyList<ScheduledSendListItem>> ListAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var rows = await db.ScheduledSends.AsNoTracking()
            .Where(x => x.MailAccountId == accountId)
            .OrderBy(x => x.SendAtUtc)
            .ThenByDescending(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        return rows.Select(x => new ScheduledSendListItem(
            x.Id, Deserialize(x.ToAddressesJson), Deserialize(x.CcAddressesJson), Deserialize(x.BccAddressesJson),
            x.Subject, x.SendAtUtc, x.Status, x.CreatedAtUtc, x.SentMailId, x.FailureReason))
            .ToList();
    }

    public async Task CancelAsync(Guid accountId, Guid id, CancellationToken cancellationToken)
    {
        var entity = await db.ScheduledSends.Include(x => x.Attachments)
            .SingleOrDefaultAsync(x => x.Id == id && x.MailAccountId == accountId, cancellationToken)
            ?? throw new InvalidOperationException("scheduled_send_not_found");
        if (entity.Status != ScheduledSendStatus.Pending)
            throw new InvalidOperationException("scheduled_send_already_sent");

        entity.Status = ScheduledSendStatus.Cancelled;
        var attachments = entity.Attachments.ToList();
        db.ScheduledSendAttachments.RemoveRange(attachments);
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(accountId, AuditActions.ScheduledSendCancelled, "ScheduledSend", entity.Id.ToString(), null, null, cancellationToken);

        foreach (var attachment in attachments)
        {
            try
            {
                await storage.DeleteAsync(attachment.StoragePath, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete staged attachment for cancelled scheduled send {ScheduledSendIdHash}.", ShortHash(id));
            }
        }
    }

    /// <summary>
    /// Produces a short, non-reversible identifier for log correlation without emitting the raw
    /// account/entity GUID in cleartext (CWE-532).
    /// </summary>
    private static string ShortHash(Guid value) => Convert.ToHexString(SHA256.HashData(value.ToByteArray()))[..8];

    internal static IReadOnlyList<string> Deserialize(string json) =>
        JsonSerializer.Deserialize<List<string>>(json) ?? [];
}
