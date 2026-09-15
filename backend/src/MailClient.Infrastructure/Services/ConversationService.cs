using MailClient.Application.Conversations;
using MailClient.Domain.Entities;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using MailEntity = MailClient.Domain.Entities.Mail;

namespace MailClient.Infrastructure.Services;

public sealed class ConversationService(AppDbContext db)
{
    /// <summary>Deterministic, idempotent conversation assignment. Safe to call repeatedly for the same mail.</summary>
    public async Task AssignAsync(Guid mailId, CancellationToken cancellationToken)
    {
        var mail = await db.Mails
            .SingleOrDefaultAsync(item => item.Id == mailId, cancellationToken);
        if (mail is null)
            return;

        var normalizedSubject = ConversationEngine.NormalizeSubject(mail.Subject);
        var references = ConversationEngine.ParseReferences(mail.References);
        var normalizedMessageId = ConversationEngine.NormalizeMessageId(mail.MessageId);
        var normalizedInReplyTo = ConversationEngine.NormalizeMessageId(mail.InReplyToMessageId);

        var target = await FindByGraphAsync(mail, normalizedMessageId, normalizedInReplyTo, references, cancellationToken)
            ?? await FindBySubjectFallbackAsync(mail, normalizedSubject, cancellationToken);

        mail.ConversationId = target?.Id;

        if (target is null)
        {
            target = new Conversation
            {
                Id = Guid.NewGuid(),
                MailAccountId = mail.MailAccountId,
                NormalizedSubject = normalizedSubject,
                StartedAt = mail.SentAt,
                LastMessageAt = mail.SentAt
            };
            db.Conversations.Add(target);
            mail.ConversationId = target.Id;
        }
        else
        {
            target.NormalizedSubject = normalizedSubject;
            var assigningSentAt = mail.SentAt;
            target.StartedAt = target.StartedAt < assigningSentAt ? target.StartedAt : assigningSentAt;
            target.LastMessageAt = target.LastMessageAt > assigningSentAt ? target.LastMessageAt : assigningSentAt;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<Conversation?> FindByGraphAsync(
        MailEntity mail,
        string normalizedMessageId,
        string normalizedInReplyTo,
        IReadOnlyList<string> references,
        CancellationToken cancellationToken)
    {
        if (normalizedInReplyTo.Length > 0
            && await FindConversationByMessageIdAsync(mail.MailAccountId, normalizedInReplyTo, mail.Id, cancellationToken) is { } parent)
            return parent;

        foreach (var reference in references)
            if (await FindConversationByMessageIdAsync(mail.MailAccountId, reference, mail.Id, cancellationToken) is { } ancestor)
                return ancestor;

        if (normalizedMessageId.Length > 0)
        {
            var childConversationId = await db.Mails
                .Where(item => item.MailAccountId == mail.MailAccountId
                    && item.Id != mail.Id
                    && item.InReplyToMessageId == normalizedMessageId
                    && item.ConversationId != null)
                .Select(item => item.ConversationId)
                .FirstOrDefaultAsync(cancellationToken);
            if (childConversationId is { } childId)
                return await db.Conversations
                    .SingleAsync(item => item.Id == childId, cancellationToken);
        }

        return null;
    }

    /// <summary>Subject fallback requires the same normalized subject AND a shared normalized participant address.</summary>
    private async Task<Conversation?> FindBySubjectFallbackAsync(MailEntity mail, string normalizedSubject, CancellationToken cancellationToken)
    {
        var mailAddresses = await db.Participants
            .Where(participant => participant.MailId == mail.Id && participant.NormalizedAddress != "")
            .Select(participant => participant.NormalizedAddress)
            .ToListAsync(cancellationToken);
        if (mailAddresses.Count == 0)
            return null;

        var candidates = await db.Conversations
            .AsNoTracking()
            .Where(item => item.MailAccountId == mail.MailAccountId && item.NormalizedSubject == normalizedSubject)
            .OrderByDescending(item => item.LastMessageAt)
            .Take(20)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);

        foreach (var candidateId in candidates)
        {
            var conversation = await db.Conversations
                .SingleAsync(item => item.Id == candidateId, cancellationToken);
            var shared = await db.Participants
                .Where(participant => db.Mails.Any(item => item.Id == participant.MailId
                    && item.MailAccountId == mail.MailAccountId
                    && item.ConversationId == candidateId))
                .Select(participant => participant.NormalizedAddress)
                .AnyAsync(address => mailAddresses.Contains(address), cancellationToken);
            if (shared)
                return conversation;
        }

        return null;
    }

    private async Task<Conversation?> FindConversationByMessageIdAsync(
        Guid accountId,
        string messageId,
        Guid excludeMailId,
        CancellationToken cancellationToken)
    {
        var conversationId = await db.Mails
            .Where(item => item.MailAccountId == accountId
                && item.Id != excludeMailId
                && item.MessageId == messageId
                && item.ConversationId != null)
            .Select(item => item.ConversationId)
            .FirstOrDefaultAsync(cancellationToken);
        return conversationId is null
            ? null
            : await db.Conversations.SingleAsync(item => item.Id == conversationId, cancellationToken);
    }
}
