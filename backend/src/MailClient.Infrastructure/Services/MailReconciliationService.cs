using System.Security.Cryptography;
using MailClient.Infrastructure.Email;
using MailKit.Search;
using MimeKit;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Infrastructure.Services;

public sealed class MailReconciliationService(AppDbContext db)
{
    public async Task<bool> ReconcileAsync(Guid accountId, Guid destinationFolderId, string? messageId, uint destinationUid, uint destinationUidValidity, CancellationToken cancellationToken, string? fingerprint = null, IRemoteMailFolder? remote = null)
    {
        if (destinationUid == 0 || destinationUidValidity == 0)
            return false;
        var pending = db.Mails.Where(mail => mail.MailAccountId == accountId
            && mail.ExpectedMailFolderId == destinationFolderId
            && mail.ReconciliationState == MailReconciliationState.Pending);
        if (!string.IsNullOrWhiteSpace(messageId))
            pending = pending.Where(mail => mail.MessageId == messageId);
        else if (fingerprint is not null && remote is not null)
            pending = pending.Where(mail => mail.ReconciliationFingerprint == fingerprint);
        else
            return false;
        var matches = await pending.Take(2).ToListAsync(cancellationToken);
        if (matches.Count != 1)
            return false;
        if (string.IsNullOrWhiteSpace(messageId)
            && !await IsUniqueFingerprintAsync(remote!, destinationUid, fingerprint!, cancellationToken))
            return false;

        var mail = matches[0];
        mail.MailFolderId = destinationFolderId;
        mail.Uid = destinationUid;
        mail.UidValidity = destinationUidValidity;
        mail.ExpectedMailFolderId = null;
        mail.ReconciliationState = MailReconciliationState.None;
        if (mail.IsRestoreReconciliation)
            mail.PreviousMailFolderId = null;
        mail.IsRestoreReconciliation = false;
        mail.ReconciliationFingerprint = null;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // A missing Message-ID has no provider identity. Only exact MIME content
    // that occurs once in the destination can safely rebind the stable mail ID.
    private static async Task<bool> IsUniqueFingerprintAsync(IRemoteMailFolder remote, uint destinationUid, string fingerprint, CancellationToken cancellationToken)
    {
        var uids = await remote.SearchAsync(SearchQuery.All, cancellationToken);
        if (!uids.Any(uid => uid.Id == destinationUid))
            return false;
        foreach (var uid in uids)
        {
            if (uid.Id == destinationUid)
                continue;
            var message = await remote.GetMessageAsync(uid, cancellationToken);
            if (await FingerprintAsync(message, cancellationToken) == fingerprint)
                return false;
        }
        return true;
    }

    public static async Task<string> FingerprintAsync(MimeMessage message, CancellationToken cancellationToken)
    {
        using var hash = SHA256.Create();
        await using var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
        var options = FormatOptions.Default.Clone();
        options.NewLineFormat = NewLineFormat.Dos;
        await message.WriteToAsync(options, stream, cancellationToken);
        await stream.FlushFinalBlockAsync(cancellationToken);
        return Convert.ToHexString(hash.Hash!);
    }
}
