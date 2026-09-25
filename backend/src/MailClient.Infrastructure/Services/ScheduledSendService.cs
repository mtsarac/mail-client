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
    string? FailureReason,
    int AttemptCount,
    DateTime? NextAttemptAtUtc);

public sealed record ScheduledSendAttachmentInfo(Guid Id, string FileName, string ContentType, long SizeBytes);

public sealed record ScheduledSendDetail(
    Guid Id,
    IReadOnlyList<string> To,
    IReadOnlyList<string> Cc,
    IReadOnlyList<string> Bcc,
    string Subject,
    string? BodyHtml,
    string? BodyText,
    DateTime SendAtUtc,
    ScheduledSendStatus Status,
    string? FailureReason,
    int AttemptCount,
    DateTime? NextAttemptAtUtc,
    IReadOnlyList<ScheduledSendAttachmentInfo> Attachments);

public sealed record RescheduleFailedSend(
    DateTimeOffset SendAtUtc,
    IReadOnlyList<string> To,
    IReadOnlyList<string> Cc,
    IReadOnlyList<string> Bcc,
    string Subject,
    string? BodyHtml,
    string? BodyText,
    IReadOnlyList<Guid>? AttachmentIds);

public sealed record ScheduledSendEdit(
    DateTime SendAtUtc,
    IReadOnlyList<string> To,
    IReadOnlyList<string> Cc,
    IReadOnlyList<string> Bcc,
    string Subject,
    string? BodyHtml,
    string? BodyText,
    IReadOnlyList<Guid> KeepAttachmentIds,
    IReadOnlyList<SendMailAttachment> NewAttachments);

/// <summary>
/// Creates, lists, edits and cancels scheduled sends. The actual delivery at <see cref="ScheduledSend.SendAtUtc"/> is
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
        var identity = command.IdentityId is { } identityId
            ? await db.MailIdentities.AsNoTracking().SingleOrDefaultAsync(x => x.Id == identityId && x.MailAccountId == accountId, cancellationToken)
                ?? throw new InvalidOperationException("identity_not_found")
            : null;

        var hashed = await ComposeMailValidator.HashAttachmentsAsync(command.Attachments, cancellationToken);
        var fingerprintRecipients = string.Join(',', command.To.Concat(command.Cc).Concat(command.Bcc));
        var fingerprint = SendOperationStore.Fingerprint(
            account.Id,
            $"{fingerprintRecipients}|{command.ReplySourceMailId}|{command.IdentityId}|{sendAtUtc:O}",
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
        TrialBuild(account, identity, recipients, subject, command.BodyHtml, command.BodyText, command.Attachments);

        ComposeMailValidator.RewindAttachments(command.Attachments);
        var scheduledSendId = Guid.NewGuid();
        var attachmentRows = new List<ScheduledSendAttachment>();
        foreach (var attachment in command.Attachments)
            attachmentRows.Add(await StageAsync(accountId, scheduledSendId, attachment, limits, cancellationToken));

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
            IdentityId = command.IdentityId,
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
            x.Subject, x.SendAtUtc, x.Status, x.CreatedAtUtc, x.SentMailId, x.FailureReason,
            x.AttemptCount, x.NextAttemptAtUtc))
            .ToList();
    }

    public async Task<ScheduledSendDetail> GetAsync(Guid accountId, Guid id, CancellationToken cancellationToken)
    {
        var entity = await db.ScheduledSends.AsNoTracking().Include(x => x.Attachments)
            .SingleOrDefaultAsync(x => x.Id == id && x.MailAccountId == accountId, cancellationToken)
            ?? throw new InvalidOperationException("scheduled_send_not_found");
        return new ScheduledSendDetail(entity.Id,
            Deserialize(entity.ToAddressesJson), Deserialize(entity.CcAddressesJson), Deserialize(entity.BccAddressesJson),
            entity.Subject, entity.BodyHtml, entity.BodyText, entity.SendAtUtc, entity.Status, entity.FailureReason,
            entity.AttemptCount, entity.NextAttemptAtUtc,
            entity.Attachments.Select(x => new ScheduledSendAttachmentInfo(x.Id, x.FileName, x.ContentType, x.SizeBytes)).ToList());
    }

    public async Task<ScheduledSendCreateResult> RescheduleFailedAsync(
        Guid accountId, Guid id, RescheduleFailedSend request, string newKey, string? correlationId,
        CancellationToken cancellationToken)
    {
        var source = await db.ScheduledSends.AsNoTracking().Include(x => x.Attachments)
            .SingleOrDefaultAsync(x => x.Id == id && x.MailAccountId == accountId, cancellationToken)
            ?? throw new InvalidOperationException("scheduled_send_not_found");
        if (source.Status != ScheduledSendStatus.Failed)
            throw new InvalidOperationException("scheduled_send_already_sent");
        ComposeMailValidator.ValidateIdempotencyKey(newKey);
        if (newKey == source.IdempotencyKey)
            throw new InvalidOperationException("idempotency_conflict");

        var selected = request.AttachmentIds is null
            ? source.Attachments.ToList()
            : source.Attachments.Where(x => request.AttachmentIds.Contains(x.Id)).ToList();
        if (request.AttachmentIds is not null && (request.AttachmentIds.Count != selected.Count
            || request.AttachmentIds.Count != request.AttachmentIds.Distinct().Count()))
            throw new InvalidOperationException("scheduled_send_attachment_not_found");

        var opened = new List<SendMailAttachment>(selected.Count);
        try
        {
            foreach (var attachment in selected)
                opened.Add(new SendMailAttachment(attachment.FileName, attachment.ContentType,
                    await storage.OpenReadAsync(attachment.StoragePath, cancellationToken)));
            var command = new SendMailCommand(accountId, request.To, request.Cc, request.Bcc, request.Subject,
                request.BodyHtml, request.BodyText, opened, source.ReplySourceMailId)
            { IdempotencyKey = newKey, IdentityId = source.IdentityId };
            return await CreateAsync(accountId, command, request.SendAtUtc.UtcDateTime, correlationId, cancellationToken);
        }
        finally
        {
            foreach (var attachment in opened)
                await attachment.Content.DisposeAsync();
        }
    }

    public async Task<ScheduledSendCreateResult> UpdatePendingAsync(
        Guid accountId, Guid id, ScheduledSendEdit edit, string? correlationId, CancellationToken cancellationToken)
    {
        var entity = await db.ScheduledSends.Include(x => x.Attachments)
            .SingleOrDefaultAsync(x => x.Id == id && x.MailAccountId == accountId, cancellationToken)
            ?? throw new InvalidOperationException("scheduled_send_not_found");
        if (entity.Status != ScheduledSendStatus.Pending)
            throw new InvalidOperationException(NotEditableCode(entity.Status));
        if (edit.SendAtUtc <= DateTime.UtcNow)
            throw new InvalidOperationException("scheduled_send_in_past");

        var limits = (await operationSettings.GetAsync(cancellationToken)).Settings.Limits;
        var recipients = MailRecipientResolver.Parse(edit.To, edit.Cc, edit.Bcc);
        ComposeMailValidator.ValidateBody(edit.BodyHtml, edit.BodyText, limits);
        var subject = ComposeMailValidator.ValidateSubject(edit.Subject);
        var kept = entity.Attachments.Where(x => edit.KeepAttachmentIds.Contains(x.Id)).ToList();
        if (kept.Count != edit.KeepAttachmentIds.Count || kept.Count != edit.KeepAttachmentIds.Distinct().Count())
            throw new InvalidOperationException("scheduled_send_attachment_not_found");
        var account = await db.MailAccounts.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == accountId && item.Status == MailAccountStatus.Active, cancellationToken)
            ?? throw new InvalidOperationException("mail_account_not_found");
        var identity = entity.IdentityId is { } entityIdentityId
            ? await db.MailIdentities.AsNoTracking().SingleOrDefaultAsync(x => x.Id == entityIdentityId && x.MailAccountId == accountId, cancellationToken)
            : null;

        var staged = new List<ScheduledSendAttachment>(edit.NewAttachments.Count);
        var opened = new List<SendMailAttachment>(kept.Count);
        try
        {
            foreach (var attachment in kept)
                opened.Add(new SendMailAttachment(attachment.FileName, attachment.ContentType,
                    await storage.OpenReadAsync(attachment.StoragePath, cancellationToken)));
            var combined = opened.Concat(edit.NewAttachments).ToList();
            ComposeMailValidator.ValidateAttachments(combined, limits);
            TrialBuild(account, identity, recipients, subject, edit.BodyHtml, edit.BodyText, combined);

            ComposeMailValidator.RewindAttachments(edit.NewAttachments);
            foreach (var attachment in edit.NewAttachments)
                staged.Add(await StageAsync(accountId, entity.Id, attachment, limits, cancellationToken));
        }
        catch
        {
            await DeleteFilesAsync(staged.Select(x => x.StoragePath), id);
            throw;
        }
        finally
        {
            foreach (var attachment in opened)
                await attachment.Content.DisposeAsync();
        }

        var removed = entity.Attachments.Except(kept).ToList();
        entity.ToAddressesJson = JsonSerializer.Serialize(edit.To);
        entity.CcAddressesJson = JsonSerializer.Serialize(edit.Cc);
        entity.BccAddressesJson = JsonSerializer.Serialize(edit.Bcc);
        entity.Subject = subject;
        entity.BodyHtml = edit.BodyHtml;
        entity.BodyText = edit.BodyText;
        entity.SendAtUtc = edit.SendAtUtc;
        entity.AttemptCount = 0;
        entity.NextAttemptAtUtc = null;
        entity.FailureReason = null;
        entity.Revision++;
        db.ScheduledSendAttachments.RemoveRange(removed);
        db.ScheduledSendAttachments.AddRange(staged);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is DbUpdateException)
        {
            await DeleteFilesAsync(staged.Select(x => x.StoragePath), id);
            if (ex is not DbUpdateConcurrencyException)
                throw;
            db.ChangeTracker.Clear();
            var stagedIds = staged.Select(x => x.Id).ToHashSet();
            var persistedStaged = await db.ScheduledSendAttachments
                .Where(x => stagedIds.Contains(x.Id)).ToListAsync(CancellationToken.None);
            db.ScheduledSendAttachments.RemoveRange(persistedStaged);
            var existingIds = await db.ScheduledSendAttachments.Where(x => x.ScheduledSendId == id)
                .Select(x => x.Id).ToListAsync(CancellationToken.None);
            db.ScheduledSendAttachments.AddRange(removed.Where(x => !existingIds.Contains(x.Id)));
            await db.SaveChangesAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            var current = await db.ScheduledSends.AsNoTracking()
                .Where(x => x.Id == id && x.MailAccountId == accountId)
                .Select(x => (ScheduledSendStatus?)x.Status)
                .SingleOrDefaultAsync(CancellationToken.None);
            throw new InvalidOperationException(current switch
            {
                null => "scheduled_send_not_found",
                ScheduledSendStatus.Pending => "scheduled_send_modified",
                var status => NotEditableCode(status.Value)
            });
        }

        await audit.WriteAsync(accountId, AuditActions.ScheduledSendUpdated, "ScheduledSend", entity.Id.ToString(), null, correlationId, cancellationToken);
        await DeleteFilesAsync(removed.Select(x => x.StoragePath), id);
        return new(entity.Id, entity.SendAtUtc, entity.Status);
    }

    public async Task CancelAsync(Guid accountId, Guid id, CancellationToken cancellationToken)
    {
        var entity = await db.ScheduledSends.Include(x => x.Attachments)
            .SingleOrDefaultAsync(x => x.Id == id && x.MailAccountId == accountId, cancellationToken)
            ?? throw new InvalidOperationException("scheduled_send_not_found");
        if (entity.Status is not (ScheduledSendStatus.Pending or ScheduledSendStatus.Failed))
            throw new InvalidOperationException("scheduled_send_already_sent");

        if (db.Database.IsRelational())
        {
            if (await db.ScheduledSends.Where(x => x.Id == id && x.MailAccountId == accountId
                    && (x.Status == ScheduledSendStatus.Pending || x.Status == ScheduledSendStatus.Failed))
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, ScheduledSendStatus.Cancelled),
                    cancellationToken) != 1)
                throw new InvalidOperationException("scheduled_send_already_sent");
        }
        else
        {
            entity.Status = ScheduledSendStatus.Cancelled;
            await db.SaveChangesAsync(cancellationToken);
        }

        var attachments = await db.ScheduledSendAttachments.Where(x => x.ScheduledSendId == id).ToListAsync(cancellationToken);
        db.ScheduledSendAttachments.RemoveRange(attachments);
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(accountId, AuditActions.ScheduledSendCancelled, "ScheduledSend", entity.Id.ToString(), null, null, cancellationToken);
        await DeleteFilesAsync(attachments.Select(x => x.StoragePath), id);
    }

    internal static string DispatchKey(ScheduledSend entity) =>
        entity.Revision == 0 ? entity.IdempotencyKey : $"scheduled-send:{entity.Id:N}:{entity.Revision}";

    private static string NotEditableCode(ScheduledSendStatus status) =>
        status is ScheduledSendStatus.Sent or ScheduledSendStatus.DeliveryUnknown
            ? "scheduled_send_already_sent"
            : "scheduled_send_not_pending";

    private void TrialBuild(MailAccount account, MailIdentity? identity, ResolvedRecipients recipients, string subject,
        string? bodyHtml, string? bodyText, IReadOnlyList<SendMailAttachment> attachments)
    {
        ComposeMailValidator.RewindAttachments(attachments);
        try
        {
            var to = recipients.To.Select(recipient => new MailboxAddress(recipient.DisplayName, recipient.Address)).ToList();
            var cc = recipients.Cc.Select(recipient => new MailboxAddress(recipient.DisplayName, recipient.Address)).ToList();
            var bcc = recipients.Bcc.Select(recipient => new MailboxAddress(recipient.DisplayName, recipient.Address)).ToList();
            MimeMessageBuilder.Build(identity?.EmailAddress ?? account.EmailAddress,
                string.IsNullOrWhiteSpace(identity?.DisplayName) ? account.DisplayName : identity.DisplayName,
                to, cc, bcc, subject, bodyHtml, bodyText, attachments, null, null, replyTo: identity?.ReplyTo);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Scheduled send message could not be constructed for account {AccountIdHash}.", ShortHash(account.Id));
            throw new InvalidOperationException("message_not_constructible");
        }
    }

    private async Task<ScheduledSendAttachment> StageAsync(Guid accountId, Guid scheduledSendId,
        SendMailAttachment attachment, RuntimeLimitSettings limits, CancellationToken cancellationToken)
    {
        var stored = await storage.SaveAsync(
            accountId, scheduledSendId, Guid.NewGuid(),
            (stream, ct) => attachment.Content.CopyToAsync(stream, ct),
            limits.MaxAttachmentBytes, cancellationToken);
        return new ScheduledSendAttachment
        {
            Id = Guid.NewGuid(),
            ScheduledSendId = scheduledSendId,
            FileName = MailFieldNormalizer.FileName(attachment.FileName),
            ContentType = MailFieldNormalizer.ContentType(attachment.ContentType),
            StoragePath = stored.RelativePath,
            SizeBytes = stored.SizeBytes
        };
    }

    private async Task DeleteFilesAsync(IEnumerable<string> paths, Guid id)
    {
        foreach (var path in paths)
        {
            try
            {
                await storage.DeleteAsync(path, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete staged attachment for scheduled send {ScheduledSendIdHash}.", ShortHash(id));
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
