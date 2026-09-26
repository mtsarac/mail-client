using System.Buffers;
using System.Security.Cryptography;
using MailClient.Application.Mail;
using MailClient.Application.Runtime;
using MailClient.Infrastructure.Email;

namespace MailClient.Infrastructure.Services;

/// <summary>
/// Multipart-compose validation shared by immediate send (<see cref="MailSendService"/>) and scheduled-send
/// creation (<see cref="ScheduledSendService"/>), so both paths reject malformed requests with the same
/// contract codes instead of re-implementing the checks.
/// </summary>
internal static class ComposeMailValidator
{
    public static void ValidateIdempotencyKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("idempotency_key_required");
        if (key.Length > SendOperationStore.MaxKeyLength)
            throw new InvalidOperationException("idempotency_key_too_long");
    }

    public static void ValidateBody(string? bodyHtml, string? bodyText, RuntimeLimitSettings limits)
    {
        if (string.IsNullOrWhiteSpace(bodyHtml) && string.IsNullOrWhiteSpace(bodyText))
            throw new InvalidOperationException("body_required");
        if ((bodyHtml?.Length ?? 0) > limits.MaxSendBodyChars || (bodyText?.Length ?? 0) > limits.MaxSendBodyChars)
            throw new InvalidOperationException("body_too_large");
    }

    public static void ValidateAttachments(IReadOnlyList<SendMailAttachment> attachments, RuntimeLimitSettings limits)
    {
        if (attachments.Count > RuntimeLimitSettings.MaxAttachmentCount)
            throw new InvalidOperationException("too_many_attachments");
        if (!AttachmentsWithinLimits(attachments, limits))
            throw new InvalidOperationException("attachment_too_large");
    }

    public static string ValidateSubject(string subject) =>
        subject.Contains('\r') || subject.Contains('\n')
            ? throw new InvalidOperationException("invalid_mail_header")
            : MailFieldNormalizer.Truncate(subject.Trim(), MailFieldLimits.Subject);

    public static bool AttachmentsWithinLimits(IReadOnlyList<SendMailAttachment> attachments, RuntimeLimitSettings limits)
    {
        var total = 0L;
        foreach (var attachment in attachments)
        {
            if (!attachment.Content.CanSeek || attachment.Content.Length > limits.MaxAttachmentBytes)
                return false;
            total += attachment.Content.Length;
        }

        return total <= limits.MaxMessageAttachmentBytes;
    }

    public static void RewindAttachments(IReadOnlyList<SendMailAttachment> attachments)
    {
        foreach (var attachment in attachments)
            if (attachment.Content.CanSeek)
                attachment.Content.Position = 0;
    }

    public static async Task<IReadOnlyList<(string FileName, string ContentType, long SizeBytes, string ContentHash)>> HashAttachmentsAsync(
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
}
