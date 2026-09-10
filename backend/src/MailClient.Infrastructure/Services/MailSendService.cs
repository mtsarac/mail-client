using MailClient.Application;
using MailClient.Application.Interfaces;
using MailClient.Application.Sync;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Email;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MimeKit;

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

        if (command.IdempotencyKey is null)
            return await SendAndStoreAsync(account, to, subject, command, operation: null, claimedAt: default, cancellationToken);

        if (command.IdempotencyKey.Length > SendOperationStore.MaxKeyLength)
            return ServiceResult<SendMailResult>.Failure(
                ServiceOutcome.Invalid, "idempotencyKey", "Idempotency key is too long.");

        var fingerprint = SendOperationStore.Fingerprint(
            account.Id,
            to.Address,
            subject,
            command.BodyHtml,
            command.BodyText,
            command.Attachments.Select(attachment => (
                MailFieldNormalizer.FileName(attachment.FileName),
                MailFieldNormalizer.ContentType(attachment.ContentType),
                attachment.Content.Length)).ToList());
        var claim = await operations.ClaimAsync(
            userId, account.Id, command.IdempotencyKey, fingerprint, cancellationToken);
        return claim switch
        {
            SendOperationStore.Replay replay => ServiceResult<SendMailResult>.Success(
                new SendMailResult(true, replay.Operation.SentCopySaved, replay.Operation.Warning)),
            SendOperationStore.Denied denied => ServiceResult<SendMailResult>.Failure(
                ServiceOutcome.Conflict, "idempotencyKey", denied.Reason),
            SendOperationStore.Proceed proceed => await SendAndStoreAsync(
                account, to, subject, command, proceed.Operation, proceed.ClaimedAt, cancellationToken),
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
        DateTime claimedAt,
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
                await operations.TryFailAsync(operation.Id, claimedAt, "The message could not be constructed.", cancellationToken);
            return ServiceResult<SendMailResult>.Failure(
                ServiceOutcome.Invalid, "body", "The message could not be constructed.");
        }

        try
        {
            await transport.SendAsync(account, message, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SMTP send failed for account {AccountId}.", account.Id);
            if (operation is not null)
                await operations.TryFailAsync(operation.Id, claimedAt, "The message could not be sent.", cancellationToken);
            return ServiceResult<SendMailResult>.Success(
                new SendMailResult(false, false, "The message could not be sent."));
        }

        if (!account.SaveSentCopy)
        {
            if (operation is not null)
                await operations.TryCompleteAsync(
                    operation.Id, claimedAt, SendOperationStatus.Sent,
                    false, null, cancellationToken);
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
                    operation.Id, claimedAt, SendOperationStatus.Sent,
                    false, "Message was sent, but no Sent folder is configured.", cancellationToken);
            return ServiceResult<SendMailResult>.Success(new SendMailResult(
                true, false, "Message was sent, but no Sent folder is configured."));
        }

        try
        {
            RewindAttachments(command.Attachments);
            await transport.AppendToSentAsync(account, sentFullName, message, cancellationToken);
            if (operation is not null)
                await operations.TryCompleteAsync(
                    operation.Id, claimedAt, SendOperationStatus.SentWithCopy,
                    true, null, cancellationToken);
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
                    operation.Id, claimedAt, SendOperationStatus.Sent,
                    false, "Message was sent, but the Sent copy could not be stored.", cancellationToken);
            return ServiceResult<SendMailResult>.Success(new SendMailResult(
                true, false, "Message was sent, but the Sent copy could not be stored."));
        }
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
