using MailClient.Application.Conversations;
using MailClient.Domain.Entities;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using MailEntity = MailClient.Domain.Entities.Mail;

namespace MailClient.Infrastructure.Services;

public sealed class ConversationService(AppDbContext db)
{
    public static readonly TimeSpan SubjectFallbackWindow = TimeSpan.FromDays(30);
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

        var graphTargets = await FindByGraphAsync(mail, normalizedMessageId, normalizedInReplyTo, references, cancellationToken);
        var target = graphTargets.Count > 0
            ? graphTargets[0]
            : await FindBySubjectFallbackAsync(mail, normalizedSubject, cancellationToken);

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
            mail.ConversationId = target.Id;
            target.NormalizedSubject = normalizedSubject;
            if (graphTargets.Count > 1)
                await MergeAsync(mail, target, graphTargets, cancellationToken);
            else
                WidenBounds(target, mail.SentAt);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task MergeAsync(MailEntity mail, Conversation survivor, IReadOnlyList<Conversation> graphTargets, CancellationToken cancellationToken)
    {
        var loserIds = graphTargets.Skip(1).Select(item => item.Id).ToList();
        var rebound = await db.Mails
            .Where(item => item.MailAccountId == mail.MailAccountId
                && item.ConversationId != null
                && loserIds.Contains(item.ConversationId.Value))
            .ToListAsync(cancellationToken);
        foreach (var item in rebound)
            item.ConversationId = survivor.Id;
        db.Conversations.RemoveRange(graphTargets.Skip(1));
        WidenBounds(survivor, mail.SentAt);
        foreach (var loser in graphTargets.Skip(1))
        {
            WidenBounds(survivor, loser.StartedAt);
            WidenBounds(survivor, loser.LastMessageAt);
        }
    }

    private static void WidenBounds(Conversation target, DateTime sentAt)
    {
        if (sentAt < target.StartedAt)
            target.StartedAt = sentAt;
        if (sentAt > target.LastMessageAt)
            target.LastMessageAt = sentAt;
    }

    private async Task<IReadOnlyList<Conversation>> FindByGraphAsync(
        MailEntity mail,
        string normalizedMessageId,
        string normalizedInReplyTo,
        IReadOnlyList<string> references,
        CancellationToken cancellationToken)
    {
        HashSet<string>? keys = null;
        if (references.Count > 0 || normalizedInReplyTo.Length > 0)
        {
            keys = new HashSet<string>(references, StringComparer.Ordinal);
            if (normalizedInReplyTo.Length > 0)
                keys.Add(normalizedInReplyTo);
        }
        var hasChildLookup = normalizedMessageId.Length > 0;
        if (keys is null && !hasChildLookup)
            return [];

        var scope = db.Mails.Where(item => item.MailAccountId == mail.MailAccountId
            && item.Id != mail.Id
            && item.ConversationId != null);
        IQueryable<MailEntity> matches = keys is not null && hasChildLookup
            ? scope.Where(item => keys.Contains(item.MessageId) || item.InReplyToMessageId == normalizedMessageId)
            : keys is not null
                ? scope.Where(item => keys.Contains(item.MessageId))
                : scope.Where(item => item.InReplyToMessageId == normalizedMessageId);
        var candidateIds = await matches
            .Select(item => item.ConversationId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (candidateIds.Count == 0)
            return [];

        return await db.Conversations
            .Where(item => item.MailAccountId == mail.MailAccountId && candidateIds.Contains(item.Id))
            .OrderBy(item => item.StartedAt)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Subject fallback requires a non-empty normalized subject AND a shared participant other than the mailbox owner,
    /// who appears on nearly every message and would otherwise make any two mails with equal subjects "related".
    /// </summary>
    private async Task<Conversation?> FindBySubjectFallbackAsync(MailEntity mail, string normalizedSubject, CancellationToken cancellationToken)
    {
        if (normalizedSubject.Length == 0)
            return null;
        var ownAddress = (await db.MailAccounts
            .Where(account => account.Id == mail.MailAccountId)
            .Select(account => account.EmailAddress)
            .SingleOrDefaultAsync(cancellationToken))?.Trim().ToLowerInvariant() ?? "";
        var mailAddresses = await db.Participants
            .Where(participant => participant.MailId == mail.Id && participant.NormalizedAddress != "" && participant.NormalizedAddress != ownAddress)
            .Select(participant => participant.NormalizedAddress)
            .ToListAsync(cancellationToken);
        if (mailAddresses.Count == 0)
            return null;

        var candidates = await db.Conversations
            .AsNoTracking()
            .Where(item => item.MailAccountId == mail.MailAccountId
                && item.NormalizedSubject == normalizedSubject
                && item.LastMessageAt >= mail.SentAt - SubjectFallbackWindow
                && item.LastMessageAt <= mail.SentAt + SubjectFallbackWindow)
            .OrderByDescending(item => item.LastMessageAt)
            .Take(20)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);

        foreach (var candidateId in candidates)
        {
            var conversation = await db.Conversations
                .SingleOrDefaultAsync(item => item.Id == candidateId, cancellationToken);
            if (conversation is null)
                continue;
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
}
