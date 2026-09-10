using MailClient.Application;
using MailClient.Application.Interfaces;
using MailClient.Application.Sync;
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
    MailSyncOptions options,
    ILogger<MailSendService> logger) : IMailSendService
{
    private const int MaxAttachmentCount = 20;

    public async Task<ServiceResult<SendMailResult>> SendAsync(
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
        if (command.Attachments.Count > MaxAttachmentCount)
            return ServiceResult<SendMailResult>.Failure(
                ServiceOutcome.Invalid, "attachments", $"At most {MaxAttachmentCount} attachments are allowed.");

        var sizeError = CheckAttachmentSizes(command.Attachments);
        if (sizeError is not null)
            return ServiceResult<SendMailResult>.Failure(ServiceOutcome.Invalid, "attachments", sizeError);

        var account = await db.MailAccounts.SingleOrDefaultAsync(
            item => item.Id == command.AccountId && item.UserId == userId && item.IsActive,
            cancellationToken);
        if (account is null)
            return ServiceResult<SendMailResult>.Failure(
                ServiceOutcome.NotFound, "account", "Mail account not found.");

        var message = MimeMessageBuilder.Build(
            account.EmailAddress, account.DisplayName, to,
            command.Subject.Trim(), command.BodyHtml, command.BodyText, command.Attachments);
        try
        {
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
                return ServiceResult<SendMailResult>.Success(
                    new SendMailResult(false, false, "The message could not be sent."));
            }

            if (!account.SaveSentCopy)
                return ServiceResult<SendMailResult>.Success(new SendMailResult(true, false, null));

            var sentFullName = await db.MailFolders
                .Where(folder => folder.MailAccountId == account.Id && folder.FolderType == MailFolderType.Sent)
                .Select(folder => folder.FullName)
                .SingleOrDefaultAsync(cancellationToken);
            if (sentFullName is null)
            {
                logger.LogWarning("Sent copy skipped for account {AccountId}: no Sent folder discovered.", account.Id);
                return ServiceResult<SendMailResult>.Success(new SendMailResult(
                    true, false, "Message was sent, but no Sent folder is configured."));
            }

            try
            {
                RewindAttachments(command.Attachments);
                await transport.AppendToSentAsync(account, sentFullName, message, cancellationToken);
                return ServiceResult<SendMailResult>.Success(new SendMailResult(true, true, null));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Sent append failed for account {AccountId}.", account.Id);
                return ServiceResult<SendMailResult>.Success(new SendMailResult(
                    true, false, "Message was sent, but the Sent copy could not be stored."));
            }
        }
        finally
        {
            foreach (var attachment in command.Attachments)
                await attachment.Content.DisposeAsync();
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
