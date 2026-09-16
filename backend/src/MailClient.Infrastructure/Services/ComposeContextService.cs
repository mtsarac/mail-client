using MailClient.Application.Conversations;
using MailClient.Application.Mail;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Infrastructure.Services;

public sealed class ComposeContextService(AppDbContext db)
{
    public async Task<ComposeContextResponse?> GetAsync(Guid accountId, Guid mailId, ComposeMode mode, CancellationToken cancellationToken)
    {
        var mail = await db.Mails.AsNoTracking().Include(x => x.Participants).Include(x => x.Attachments)
            .SingleOrDefaultAsync(x => x.Id == mailId && x.MailAccountId == accountId, cancellationToken);
        if (mail is null)
            return null;
        var recipients = mode == ComposeMode.ReplyAll
            ? MailRecipientResolver.ReplyAll(mail, (await db.MailAccounts.Where(x => x.Id == accountId).Select(x => x.EmailAddress).SingleAsync(cancellationToken)))
            : mode == ComposeMode.Reply ? MailRecipientResolver.Reply(mail) : ResolvedRecipients.Empty;
        var subject = mode == ComposeMode.Forward
            ? $"Fwd: {ConversationEngine.NormalizeSubject(mail.Subject)}"
            : $"Re: {ConversationEngine.NormalizeSubject(mail.Subject)}";
        return new(mail.Id, recipients.To, recipients.Cc, subject, mode == ComposeMode.Reply || mode == ComposeMode.ReplyAll ? mail.MessageId : string.Empty,
            mode == ComposeMode.Reply || mode == ComposeMode.ReplyAll ? string.Join(' ', ConversationEngine.ParseReferences(mail.References).Append(ConversationEngine.NormalizeMessageId(mail.MessageId))) : string.Empty,
            mail.FromAddress, mail.SentAt.ToString("O"), mail.Subject,
            mail.Attachments.OrderBy(x => x.FileName).Select(x => new AttachmentResponse(x.Id, x.FileName, x.ContentType, x.SizeBytes, x.IsInline, x.ContentId, x.ContentDisposition)).ToList());
    }
}
