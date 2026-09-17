using System.Buffers;
using System.Security.Cryptography;
using MailClient.Application.Conversations;
using MailClient.Application.Mail;
using MailClient.Application.Runtime;
using MailClient.Infrastructure.Runtime;
using MailClient.Application.Sync;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Email;
using MailClient.Infrastructure.Observability;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace MailClient.Infrastructure.Services;

public sealed record SendMailResult(bool Sent, bool SentCopySaved, string? Warning);

public sealed class MailSendService(
    AppDbContext db,
    Mail.IMailTransport transport,
    SendOperationStore operations,
    RuntimeOperationSettings operationSettings,
    AuditLogger audit,
    ILogger<MailSendService> logger)
{
    public MailSendService(
        AppDbContext db,
        Mail.IMailTransport transport,
        SendOperationStore operations,
        MailSyncOptions options,
        AuditLogger audit,
        ILogger<MailSendService> logger)
        : this(db, transport, operations, RuntimeOperationSettings.FromMailSyncOptions(options), audit, logger)
    {
    }

    private const int MaxAttachmentCount = 20;

    public async Task<SendMailResult> SendAsync(
        Guid accountId,
        SendMailCommand command,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await SendCoreAsync(accountId, command, correlationId, cancellationToken);
        }
        finally
        {
            foreach (var attachment in command.Attachments)
                await attachment.Content.DisposeAsync();
        }
    }

    private async Task<SendMailResult> SendCoreAsync(
        Guid accountId,
        SendMailCommand command,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var limits = (await operationSettings.GetAsync(cancellationToken)).Settings.Limits;
        var recipients = MailRecipientResolver.Parse(command.To, command.Cc, command.Bcc);
        var to = recipients.To.Select(recipient => new MailboxAddress(recipient.DisplayName, recipient.Address)).ToList();
        var cc = recipients.Cc.Select(recipient => new MailboxAddress(recipient.DisplayName, recipient.Address)).ToList();
        var bcc = recipients.Bcc.Select(recipient => new MailboxAddress(recipient.DisplayName, recipient.Address)).ToList();
        if (string.IsNullOrWhiteSpace(command.BodyHtml) && string.IsNullOrWhiteSpace(command.BodyText))
            throw new InvalidOperationException("body_required");
        if ((command.BodyHtml?.Length ?? 0) > limits.MaxSendBodyChars
            || (command.BodyText?.Length ?? 0) > limits.MaxSendBodyChars)
            throw new InvalidOperationException("body_too_large");
        if (command.Attachments.Count > MaxAttachmentCount)
            throw new InvalidOperationException("too_many_attachments");

        var sizeError = CheckAttachmentSizes(command.Attachments, limits);
        if (sizeError is not null)
            throw new InvalidOperationException("attachment_too_large");

        var subject = command.Subject.Contains('\r') || command.Subject.Contains('\n')
            ? throw new InvalidOperationException("invalid_mail_header")
            : MailFieldNormalizer.Truncate(command.Subject.Trim(), MailFieldLimits.Subject);
        var account = await db.MailAccounts.SingleOrDefaultAsync(
            item => item.Id == command.AccountId && item.Id == accountId && item.Status == MailAccountStatus.Active,
            cancellationToken)
            ?? throw new InvalidOperationException("mail_account_not_found");

        if (string.IsNullOrWhiteSpace(command.IdempotencyKey))
            throw new InvalidOperationException("idempotency_key_required");
        if (command.IdempotencyKey.Length > SendOperationStore.MaxKeyLength)
            throw new InvalidOperationException("idempotency_key_too_long");

        var threading = command.TrustedMessageId is not null
            ? new ReplyThreading(command.TrustedInReplyToMessageId, command.TrustedReferences)
            : await ResolveThreadingAsync(account.Id, command.ReplySourceMailId, cancellationToken);
        var hashed = await HashAttachmentsAsync(command.Attachments, cancellationToken);
        var fingerprintRecipients = string.Join(',', to.Select(x => x.Address).Concat(cc.Select(x => x.Address)).Concat(bcc.Select(x => x.Address)));
        var fingerprint = SendOperationStore.Fingerprint(account.Id, $"{fingerprintRecipients}|{command.ReplySourceMailId}|{command.TrustedMessageId}", subject, command.BodyHtml, command.BodyText, hashed);
        var claim = await operations.ClaimAsync(account.Id, command.IdempotencyKey, fingerprint, cancellationToken);
        return claim switch
        {
            SendOperationStore.Replay replay => new SendMailResult(true, replay.Operation.SentCopySaved, replay.Operation.Warning),
            SendOperationStore.Denied denied => throw new InvalidOperationException(denied.Reason.Contains("different request") ? "idempotency_conflict" : "send_in_progress"),
            SendOperationStore.Proceed proceed => await SendAndAuditAsync(account, to, cc, bcc, subject, command, threading, proceed.Operation, correlationId, cancellationToken),
            _ => throw new InvalidOperationException("idempotency_key_required")
        };
    }

    private async Task<SendMailResult> SendAndAuditAsync(
        MailAccount account,
        IReadOnlyList<MailboxAddress> to,
        IReadOnlyList<MailboxAddress> cc,
        IReadOnlyList<MailboxAddress> bcc,
        string subject,
        SendMailCommand command,
        ReplyThreading threading,
        SendOperation? operation,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var result = await SendAndStoreAsync(account, to, cc, bcc, subject, command, threading, operation, cancellationToken);
        if (result.Sent)
        {
            await audit.WriteAsync(account.Id, "mail.sent", "MailAccount", account.Id.ToString(),
                new Dictionary<string, string?> { ["toAddress"] = string.Join(',', to.Select(x => x.Address)), ["sentCopySaved"] = result.SentCopySaved.ToString() },
                correlationId, cancellationToken);
        }

        return result;
    }

    private async Task<SendMailResult> SendAndStoreAsync(
        MailAccount account,
        IReadOnlyList<MailboxAddress> to,
        IReadOnlyList<MailboxAddress> cc,
        IReadOnlyList<MailboxAddress> bcc,
        string subject,
        SendMailCommand command,
        ReplyThreading threading,
        SendOperation? operation,
        CancellationToken cancellationToken)
    {
        MimeMessage message;
        try
        {
            message = MimeMessageBuilder.Build(
                account.EmailAddress, account.DisplayName, to, cc, bcc,
                subject, command.BodyHtml, command.BodyText, command.Attachments,
                threading.InReplyToMessageId, threading.References, command.TrustedMessageId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Send message could not be constructed for account {AccountId}.", account.Id);
            if (operation is not null)
                await operations.TryFailAsync(operation.Id, "The message could not be constructed.", cancellationToken);
            throw new InvalidOperationException("message_not_constructible");
        }

        try
        {
            await transport.SendAsync(account, message, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await MarkUnknownBestEffortAsync(operation);
            throw;
        }
        catch (SmtpDeliveryException ex)
        {
            logger.LogWarning(ex, "SMTP delivery outcome unknown for account {AccountId}.", account.Id);
            if (operation is not null)
                await operations.TryMarkUnknownAsync(operation.Id, cancellationToken);
            throw new InvalidOperationException("delivery_unknown");
        }
        catch (MailConnectionException ex)
        {
            logger.LogWarning(ex, "SMTP send failed before delivery for account {AccountId}.", account.Id);
            if (operation is not null)
                await operations.TryFailAsync(operation.Id, "The message could not be sent.", cancellationToken);
            return new SendMailResult(false, false, "The message could not be sent.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SMTP send failed ambiguously for account {AccountId}.", account.Id);
            if (operation is not null)
                await operations.TryMarkUnknownAsync(operation.Id, cancellationToken);
            throw new InvalidOperationException("delivery_unknown");
        }

        if (operation is not null)
            await operations.TryCompleteAsync(operation.Id, SendOperationStatus.Sent, false, null, CancellationToken.None);

        if (!account.SaveSentCopy)
        {
            if (operation is not null)
                await operations.TryCompleteAsync(operation.Id, SendOperationStatus.Sent, false, null, CancellationToken.None);
            return new SendMailResult(true, false, null);
        }

        var sentFullName = await db.MailFolders
            .Where(folder => folder.MailAccountId == account.Id && folder.FolderType == MailFolderType.Sent)
            .Select(folder => folder.FullName)
            .SingleOrDefaultAsync(cancellationToken);
        if (sentFullName is null)
        {
            logger.LogWarning("Sent copy skipped for account {AccountId}: no Sent folder discovered.", account.Id);
            if (operation is not null)
                await operations.TryCompleteAsync(operation.Id, SendOperationStatus.Sent, false, "Message was sent, but no Sent folder is configured.", CancellationToken.None);
            return new SendMailResult(true, false, "Message was sent, but no Sent folder is configured.");
        }

        try
        {
            RewindAttachments(command.Attachments);
            await transport.AppendToSentAsync(account, sentFullName, message, cancellationToken);
            if (operation is not null)
                await operations.TryCompleteAsync(operation.Id, SendOperationStatus.SentWithCopy, true, null, CancellationToken.None);
            return new SendMailResult(true, true, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sent append failed for account {AccountId}.", account.Id);
            if (operation is not null)
                await operations.TryCompleteAsync(operation.Id, SendOperationStatus.Sent, false, "Message was sent, but the Sent copy could not be stored.", CancellationToken.None);
            return new SendMailResult(true, false, "Message was sent, but the Sent copy could not be stored.");
        }
    }

    private async Task MarkUnknownBestEffortAsync(SendOperation? operation)
    {
        if (operation is null)
            return;
        try
        {
            using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await operations.TryMarkUnknownAsync(operation.Id, safety.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not persist uncertain SMTP delivery state for send operation {OperationId}.", operation.Id);
        }
    }

    private sealed record ReplyThreading(string? InReplyToMessageId, string? References);

    private async Task<ReplyThreading> ResolveThreadingAsync(Guid accountId, Guid? sourceMailId, CancellationToken cancellationToken)
    {
        if (sourceMailId is not { } id)
            return new(null, null);
        var source = await db.Mails.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.MailAccountId == accountId, cancellationToken)
            ?? throw new InvalidOperationException("mail_not_found");
        var references = ConversationEngine.ParseReferences(source.References)
            .Append(ConversationEngine.NormalizeMessageId(source.MessageId))
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return new(ConversationEngine.NormalizeMessageId(source.MessageId), string.Join(' ', references));
    }

    private static async Task<IReadOnlyList<(string FileName, string ContentType, long SizeBytes, string ContentHash)>> HashAttachmentsAsync(
        IReadOnlyList<SendMailAttachment> attachments,
        CancellationToken cancellationToken)
    {
        var hashed = new List<(string, string, long, string)>(attachments.Count);
        foreach (var attachment in attachments)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                int read;
                while ((read = await attachment.Content.ReadAsync(buffer, cancellationToken)) > 0)
                    hash.AppendData(buffer, 0, read);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            hashed.Add((
                MailFieldNormalizer.FileName(attachment.FileName),
                MailFieldNormalizer.ContentType(attachment.ContentType),
                attachment.Content.Length,
                Convert.ToHexString(hash.GetHashAndReset())));
            if (attachment.Content.CanSeek)
                attachment.Content.Position = 0;
        }

        return hashed;
    }

    private static string? CheckAttachmentSizes(IReadOnlyList<SendMailAttachment> attachments, RuntimeLimitSettings limits)
    {
        var total = 0L;
        foreach (var attachment in attachments)
        {
            if (!attachment.Content.CanSeek)
                return "Attachment size could not be determined.";
            if (attachment.Content.Length > limits.MaxAttachmentBytes)
                return $"Attachment '{attachment.FileName}' exceeds the per-attachment size limit.";
            total += attachment.Content.Length;
        }

        return total > limits.MaxMessageAttachmentBytes
            ? "Attachments exceed the per-message size limit."
            : null;
    }

    private static void RewindAttachments(IReadOnlyList<SendMailAttachment> attachments)
    {
        foreach (var attachment in attachments)
            if (attachment.Content.CanSeek)
                attachment.Content.Position = 0;
    }

    private static bool HasLocalAndDomain(string address)
    {
        var at = address.LastIndexOf('@');
        return at > 0 && at < address.Length - 1;
    }
}
