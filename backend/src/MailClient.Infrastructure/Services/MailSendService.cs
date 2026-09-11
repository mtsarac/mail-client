using System.Buffers;
using System.Security.Cryptography;
using MailClient.Application;
using MailClient.Application.Interfaces;
using MailClient.Application.Network;
using MailClient.Application.Sync;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Email;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MimeKit;

// Validates, deduplicates, and sends mail through the account's SMTP server.
namespace MailClient.Infrastructure.Services;

public sealed class MailSendService(
    AppDbContext db,
    IMailTransport transport,
    SendOperationStore operations,
    MailSyncOptions options,
    ILogger<MailSendService> logger) : IMailSendService
{
    private const int MaxAttachmentCount = 20;

    public async Task<ServiceResult<SendMailResult>> SendAsync(
        Guid userId,
        SendMailCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            return await SendCoreAsync(userId, command, cancellationToken);
        }
        finally
        {
            foreach (var attachment in command.Attachments)
                await attachment.Content.DisposeAsync();
        }
    }

    private async Task<ServiceResult<SendMailResult>> SendCoreAsync(
        Guid userId,
        SendMailCommand command,
        CancellationToken cancellationToken)
    {
        if (!MailboxAddress.TryParse(command.ToAddress.Trim(), out var to)
            || !HasLocalAndDomain(to.Address))
            return ServiceResult<SendMailResult>.Failure(
                ServiceOutcome.Invalid, "toAddress", "Recipient address is invalid.");
        if (string.IsNullOrWhiteSpace(command.BodyHtml) && string.IsNullOrWhiteSpace(command.BodyText))
            return ServiceResult<SendMailResult>.Failure(
                ServiceOutcome.Invalid, "body", "Either HTML or text body is required.");
        if ((command.BodyHtml?.Length ?? 0) > options.MaxSendBodyChars
            || (command.BodyText?.Length ?? 0) > options.MaxSendBodyChars)
            return ServiceResult<SendMailResult>.Failure(
                ServiceOutcome.Invalid, "body", "Message body is too large.");
        if (command.Attachments.Count > MaxAttachmentCount)
            return ServiceResult<SendMailResult>.Failure(
                ServiceOutcome.Invalid, "attachments", $"At most {MaxAttachmentCount} attachments are allowed.");

        var sizeError = CheckAttachmentSizes(command.Attachments);
        if (sizeError is not null)
            return ServiceResult<SendMailResult>.Failure(ServiceOutcome.Invalid, "attachments", sizeError);

        var subject = MailFieldNormalizer.Truncate(command.Subject.Trim(), MailFieldLimits.Subject);
        var account = await db.MailAccounts.SingleOrDefaultAsync(
            item => item.Id == command.AccountId && item.UserId == userId && item.IsActive,
            cancellationToken);
        if (account is null)
            return ServiceResult<SendMailResult>.Failure(
                ServiceOutcome.NotFound, "account", "Mail account not found.");

        if (string.IsNullOrWhiteSpace(command.IdempotencyKey))
            return ServiceResult<SendMailResult>.Failure(
                ServiceOutcome.Invalid, "idempotencyKey", "Idempotency-Key header is required.");

        if (command.IdempotencyKey.Length > SendOperationStore.MaxKeyLength)
            return ServiceResult<SendMailResult>.Failure(
                ServiceOutcome.Invalid, "idempotencyKey", "Idempotency key is too long.");

        var hashed = await HashAttachmentsAsync(command.Attachments, cancellationToken);
        var fingerprint = SendOperationStore.Fingerprint(
            account.Id,
            to.Address,
            subject,
            command.BodyHtml,
            command.BodyText,
            hashed);
        var claim = await operations.ClaimAsync(
            userId, account.Id, command.IdempotencyKey, fingerprint, cancellationToken);
        return claim switch
        {
            SendOperationStore.Replay replay => ServiceResult<SendMailResult>.Success(
                new SendMailResult(true, replay.Operation.SentCopySaved, replay.Operation.Warning)),
            SendOperationStore.Denied denied => ServiceResult<SendMailResult>.Failure(
                ServiceOutcome.Conflict, "idempotencyKey", denied.Reason),
            SendOperationStore.Proceed proceed => await SendAndStoreAsync(
                account, to, subject, command, proceed.Operation, cancellationToken),
            _ => ServiceResult<SendMailResult>.Failure(
                ServiceOutcome.Invalid, "idempotencyKey", "Idempotency key could not be processed.")
        };
    }

    private async Task<ServiceResult<SendMailResult>> SendAndStoreAsync(
        MailAccount account,
        MailboxAddress to,
        string subject,
        SendMailCommand command,
        SendOperation? operation,
        CancellationToken cancellationToken)
    {
        MimeMessage message;
        try
        {
            message = MimeMessageBuilder.Build(
                account.EmailAddress, account.DisplayName, to,
                subject, command.BodyHtml, command.BodyText, command.Attachments);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Send message could not be constructed for account {AccountId}.", account.Id);
            if (operation is not null)
                await operations.TryFailAsync(operation.Id, "The message could not be constructed.", cancellationToken);
            return ServiceResult<SendMailResult>.Failure(
                ServiceOutcome.Invalid, "body", "The message could not be constructed.");
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
            return ServiceResult<SendMailResult>.Failure(
                ServiceOutcome.Conflict, "delivery",
                "Delivery status is uncertain; the message may have been sent. Retry with the same idempotency key to check.");
        }
        catch (MailConnectionException ex)
        {
            logger.LogWarning(ex, "SMTP send failed before delivery for account {AccountId}.", account.Id);
            if (operation is not null)
                await operations.TryFailAsync(operation.Id, "The message could not be sent.", cancellationToken);
            return ServiceResult<SendMailResult>.Success(
                new SendMailResult(false, false, "The message could not be sent."));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SMTP send failed ambiguously for account {AccountId}.", account.Id);
            if (operation is not null)
                await operations.TryMarkUnknownAsync(operation.Id, cancellationToken);
            return ServiceResult<SendMailResult>.Failure(
                ServiceOutcome.Conflict, "delivery",
                "Delivery status is uncertain; the message may have been sent. Retry with the same idempotency key to check.");
        }

        if (operation is not null)
            await operations.TryCompleteAsync(
                operation.Id, SendOperationStatus.Sent, false, null, CancellationToken.None);

        if (!account.SaveSentCopy)
        {
            if (operation is not null)
                await operations.TryCompleteAsync(
                    operation.Id, SendOperationStatus.Sent,
                    false, null, CancellationToken.None);
            return ServiceResult<SendMailResult>.Success(new SendMailResult(true, false, null));
        }

        var sentFullName = await db.MailFolders
            .Where(folder => folder.MailAccountId == account.Id && folder.FolderType == MailFolderType.Sent)
            .Select(folder => folder.FullName)
            .SingleOrDefaultAsync(cancellationToken);
        if (sentFullName is null)
        {
            logger.LogWarning("Sent copy skipped for account {AccountId}: no Sent folder discovered.", account.Id);
            if (operation is not null)
                await operations.TryCompleteAsync(
                    operation.Id, SendOperationStatus.Sent,
                    false, "Message was sent, but no Sent folder is configured.", CancellationToken.None);
            return ServiceResult<SendMailResult>.Success(new SendMailResult(
                true, false, "Message was sent, but no Sent folder is configured."));
        }

        try
        {
            RewindAttachments(command.Attachments);
            await transport.AppendToSentAsync(account, sentFullName, message, cancellationToken);
            if (operation is not null)
                await operations.TryCompleteAsync(
                    operation.Id, SendOperationStatus.SentWithCopy,
                    true, null, CancellationToken.None);
            return ServiceResult<SendMailResult>.Success(new SendMailResult(true, true, null));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sent append failed for account {AccountId}.", account.Id);
            if (operation is not null)
                await operations.TryCompleteAsync(
                    operation.Id, SendOperationStatus.Sent,
                    false, "Message was sent, but the Sent copy could not be stored.", CancellationToken.None);
            return ServiceResult<SendMailResult>.Success(new SendMailResult(
                true, false, "Message was sent, but the Sent copy could not be stored."));
        }
    }

    // The request token is already dead on this path by definition, so the
    // safety write runs under a short server-owned timeout instead. Only the
    // status UPDATE runs here: no SMTP, no APPEND, no other work. Failure is
    // swallowed (the row stays InProgress, which still denies resend) and the
    // original cancellation keeps propagating via the caller's rethrow.
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
            logger.LogWarning(
                ex,
                "Could not persist uncertain SMTP delivery state for send operation {OperationId}.",
                operation.Id);
        }
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

    private string? CheckAttachmentSizes(IReadOnlyList<SendMailAttachment> attachments)
    {
        var total = 0L;
        foreach (var attachment in attachments)
        {
            if (!attachment.Content.CanSeek)
                return "Attachment size could not be determined.";
            if (attachment.Content.Length > options.MaxAttachmentBytes)
                return $"Attachment '{attachment.FileName}' exceeds the per-attachment size limit.";
            total += attachment.Content.Length;
        }

        return total > options.MaxMessageAttachmentBytes
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
