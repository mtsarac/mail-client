using System.Security.Cryptography;
using MailClient.Application.Mail;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Mail;
using MailClient.Infrastructure.Persistence;
using MailKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MimeKit;
using MimeKit.Cryptography;

namespace MailClient.Infrastructure.Services;

public sealed record MailSourceResult<T>(T? Value, MailOperationError Error = MailOperationError.None);

public sealed class MailSourceService(
    AppDbContext db,
    IMailFolderClient folders,
    ILogger<MailSourceService> logger)
{
    public async Task<MailSourceResult<MailSourceHeadersResponse>> GetHeadersAsync(Guid accountId, Guid mailId, CancellationToken cancellationToken)
    {
        var fetched = await FetchAsync(accountId, mailId, cancellationToken);
        if (fetched.Value is not { } message)
            return new(null, fetched.Error);
        var headers = message.Headers
            .Select(header => new MailHeaderResponse(header.Field, header.Value.Trim()))
            .ToList();
        return new(new MailSourceHeadersResponse(headers));
    }

    public Task<MailSourceResult<byte[]>> GetRawAsync(Guid accountId, Guid mailId, CancellationToken cancellationToken) =>
        FetchAsync(accountId, mailId, async (remote, uid, ct) =>
        {
            using var stream = await remote.GetStreamAsync(uid, ct);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            return buffer.ToArray();
        }, cancellationToken);

    public async Task<MailSourceResult<MailSignatureResponse>> VerifySignatureAsync(Guid accountId, Guid mailId, CancellationToken cancellationToken)
    {
        var fetched = await FetchAsync(accountId, mailId, cancellationToken);
        if (fetched.Value is not { } message)
            return new(null, fetched.Error);

        return message.Body switch
        {
            MultipartSigned signed when IsSMimeProtocol(signed.ContentType.Parameters["protocol"])
                => new(await VerifySMimeAsync(context => signed.VerifyAsync(context, cancellationToken))),
            ApplicationPkcs7Mime pkcs7 when pkcs7.SecureMimeType == SecureMimeType.SignedData
                => new(await VerifySMimeAsync(context => Task.FromResult(pkcs7.Verify(context, out _)))),
            MultipartSigned
                => new(new MailSignatureResponse(MailCryptoStandard.OpenPgp, MailSignatureStatus.Unverifiable, [])),
            _ when message.TextBody?.Contains("-----BEGIN PGP SIGNED MESSAGE-----", StringComparison.Ordinal) == true
                => new(new MailSignatureResponse(MailCryptoStandard.OpenPgp, MailSignatureStatus.Unverifiable, [])),
            _ => new(null, MailOperationError.NotSupported)
        };
    }

    private static bool IsSMimeProtocol(string? protocol) =>
        protocol?.Trim().ToLowerInvariant() is "application/pkcs7-signature" or "application/x-pkcs7-signature";

    private static async Task<MailSignatureResponse> VerifySMimeAsync(Func<SecureMimeContext, Task<DigitalSignatureCollection>> verify)
    {
        using var context = new TemporarySecureMimeContext();
        DigitalSignatureCollection signatures;
        try
        {
            signatures = await verify(context);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException or ParseException)
        {
            return new MailSignatureResponse(MailCryptoStandard.SMime, MailSignatureStatus.Invalid, []);
        }

        if (signatures.Count == 0)
            return new MailSignatureResponse(MailCryptoStandard.SMime, MailSignatureStatus.Invalid, []);

        var status = MailSignatureStatus.Valid;
        var signers = new List<MailSignatureSigner>();
        foreach (var signature in signatures)
        {
            var certificate = signature.SignerCertificate;
            signers.Add(new MailSignatureSigner(
                string.IsNullOrWhiteSpace(certificate?.Name) ? null : certificate.Name,
                string.IsNullOrWhiteSpace(certificate?.Email) ? null : certificate.Email,
                signature.CreationDate == default ? null : signature.CreationDate.ToUniversalTime(),
                certificate is null ? null : certificate.ExpirationDate.ToUniversalTime()));
            if (!Verifies(signature, verifySignatureOnly: true))
                status = MailSignatureStatus.Invalid;
            else if (status == MailSignatureStatus.Valid && !Verifies(signature, verifySignatureOnly: false))
                status = MailSignatureStatus.Untrusted;
        }

        return new MailSignatureResponse(MailCryptoStandard.SMime, status, signers);
    }

    private static bool Verifies(IDigitalSignature signature, bool verifySignatureOnly)
    {
        try
        {
            return signature.Verify(verifySignatureOnly);
        }
        catch (DigitalSignatureVerifyException)
        {
            return false;
        }
    }

    private Task<MailSourceResult<MimeMessage>> FetchAsync(Guid accountId, Guid mailId, CancellationToken cancellationToken) =>
        FetchAsync(accountId, mailId, (remote, uid, ct) => remote.GetMessageAsync(uid, ct), cancellationToken);

    private async Task<MailSourceResult<T>> FetchAsync<T>(
        Guid accountId, Guid mailId,
        Func<Email.IRemoteMailFolder, UniqueId, CancellationToken, Task<T>> fetch,
        CancellationToken cancellationToken) where T : class
    {
        var mail = await db.Mails.AsNoTracking().Include(item => item.MailAccount).Include(item => item.MailFolder)
            .SingleOrDefaultAsync(item => item.Id == mailId && item.MailAccountId == accountId, cancellationToken);
        if (mail?.MailAccount is null || mail.MailFolder is null)
            return new(null, MailOperationError.NotFound);
        if (mail.MailAccount.Status == MailAccountStatus.NeedsReauthentication)
            return new(null, MailOperationError.NeedsReauthentication);

        try
        {
            var message = await folders.UseFolderAsync(mail.MailAccount, mail.MailFolder.FullName, false, async (remote, ct) =>
            {
                if (remote.UidValidity != mail.UidValidity)
                    return null;
                try
                {
                    return await fetch(remote, new UniqueId(mail.Uid), ct);
                }
                catch (MessageNotFoundException)
                {
                    return null;
                }
            }, cancellationToken);
            return message is null ? new(null, MailOperationError.Conflict) : new(message);
        }
        catch (Exception exception) when (exception is MailConnectionException or CryptographicException or MailKit.Net.Imap.ImapProtocolException or MailKit.Net.Imap.ImapCommandException)
        {
            logger.LogWarning(exception, "Fetching mail source failed for mail {MailId}.", mailId);
            return new(null, MailOperationError.ProviderUnavailable);
        }
    }
}
