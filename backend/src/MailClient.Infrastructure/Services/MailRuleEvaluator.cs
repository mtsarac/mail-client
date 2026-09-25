using System.Text.Json;
using MailClient.Application.Mail;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MailEntity = MailClient.Domain.Entities.Mail;

namespace MailClient.Infrastructure.Services;

public sealed class MailRuleEvaluator(AppDbContext db, IMailOperationService operations, ILogger<MailRuleEvaluator> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task EvaluatePendingAsync(Guid accountId, Guid folderId, CancellationToken cancellationToken)
    {
        var rules = await db.MailRules.AsNoTracking()
            .Where(x => x.MailAccountId == accountId && x.Enabled)
            .OrderBy(x => x.Priority).ThenBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var ids = await db.Mails.AsNoTracking()
            .Where(x => x.MailAccountId == accountId && x.MailFolderId == folderId && x.RulePending)
            .Select(x => x.Id).ToListAsync(cancellationToken);
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var mail = await db.Mails.AsNoTracking().Include(x => x.Participants).Include(x => x.MailFolder)
                    .SingleOrDefaultAsync(x => x.Id == id && x.MailAccountId == accountId, cancellationToken);
                if (mail is null) continue;
                foreach (var rule in rules)
                {
                    var conditions = JsonSerializer.Deserialize<List<RuleCondition>>(rule.ConditionJson, JsonOptions)!;
                    if (!InScope(mail, conditions) || !(rule.Logic == "And"
                        ? conditions.All(x => Matches(mail, x))
                        : conditions.Any(x => Matches(mail, x)))) continue;
                    var actions = JsonSerializer.Deserialize<List<RuleAction>>(rule.ActionJson, JsonOptions)!;
                    var stop = false;
                    foreach (var action in actions)
                    {
                        if (action.Type == "stopProcessing")
                        {
                            stop = true;
                            break;
                        }
                        await ApplyAsync(accountId, id, action, cancellationToken);
                    }
                    if (stop) break;
                }
                var processed = await db.Mails.SingleOrDefaultAsync(x => x.Id == id && x.MailAccountId == accountId, cancellationToken);
                if (processed is not null)
                {
                    processed.RulePending = false;
                    await db.SaveChangesAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Rule processing failed for account {AccountId}, mail {MailId}: {FailureType}.",
                    accountId, id, ex.GetType().Name);
                db.ChangeTracker.Clear();
            }
        }
    }

    internal static bool InScope(MailEntity mail, IReadOnlyList<RuleCondition> conditions)
    {
        var folders = conditions.Where(x => x.Type == "folder").ToList();
        return folders.Count == 0
            ? mail.MailFolder?.FolderType == MailFolderType.Inbox
            : folders.Any(x => Matches(mail, x));
    }

    internal static bool Matches(MailEntity mail, RuleCondition condition)
    {
        var value = condition.Value ?? "";
        return condition.Type switch
        {
            "senderContains" => mail.FromAddress.Contains(value, StringComparison.OrdinalIgnoreCase)
                || mail.FromDisplayName.Contains(value, StringComparison.OrdinalIgnoreCase),
            "senderEquals" => mail.FromAddress.Equals(value, StringComparison.OrdinalIgnoreCase),
            "senderDomain" => mail.FromAddress.AsSpan().EndsWith(("@" + value.TrimStart('@')).AsSpan(), StringComparison.OrdinalIgnoreCase),
            "subjectContains" => mail.Subject.Contains(value, StringComparison.OrdinalIgnoreCase),
            "recipientContains" => mail.Participants.Any(p => p.Type is ParticipantType.To or ParticipantType.Cc or ParticipantType.Bcc
                && (p.Address.Contains(value, StringComparison.OrdinalIgnoreCase)
                    || p.DisplayName.Contains(value, StringComparison.OrdinalIgnoreCase))),
            "hasAttachment" => mail.HasAttachments,
            "folder" => Guid.TryParse(value, out var folderId) && mail.MailFolderId == folderId,
            _ => false
        };
    }

    private async Task ApplyAsync(Guid accountId, Guid mailId, RuleAction action, CancellationToken cancellationToken)
    {
        if (action.Type == "addLabel")
        {
            var labelId = action.LabelId!.Value;
            if (!await db.MailLabels.AnyAsync(x => x.Id == labelId && x.MailAccountId == accountId, cancellationToken))
                return;
            if (await db.MailLabelAssignments.AnyAsync(x => x.MailId == mailId && x.MailLabelId == labelId && x.MailAccountId == accountId, cancellationToken))
                return;
            if (!await db.Mails.AnyAsync(x => x.Id == mailId && x.MailAccountId == accountId, cancellationToken))
                return;
            db.MailLabelAssignments.Add(new MailLabelAssignment { Id = Guid.NewGuid(), MailAccountId = accountId, MailId = mailId, MailLabelId = labelId });
            await db.SaveChangesAsync(cancellationToken);
            return;
        }
        var kind = action.Type switch
        {
            "markRead" => MailOperationKind.Read,
            "markUnread" => MailOperationKind.Unread,
            "star" => MailOperationKind.Star,
            "archive" => MailOperationKind.Archive,
            "move" => MailOperationKind.Move,
            "trash" => MailOperationKind.Trash,
            "spam" => MailOperationKind.Spam,
            _ => throw new InvalidOperationException("Invalid stored rule action.")
        };
        var result = await operations.ExecuteAsync(accountId, new MailOperationRequest(mailId, kind, action.FolderId), null, cancellationToken);
        if (!result.Success && result.Error is not (MailOperationError.NotFound or MailOperationError.FolderNotFound or MailOperationError.NotSupported))
            throw new InvalidOperationException($"Rule mail operation failed: {result.Error}.");
    }
}
