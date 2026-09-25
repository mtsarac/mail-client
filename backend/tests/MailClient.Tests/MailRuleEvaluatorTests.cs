using System.Text.Json;
using MailClient.Application.Mail;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MailClient.Tests;

public sealed class MailRuleEvaluatorTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Priority_And_StopProcessing_PreventLaterActions()
    {
        await using var db = CreateDb();
        var (account, folder, mail) = await SeedAsync(db);
        AddRule(db, account, 20, "Or", [new("subjectContains", "offer")], [new("trash")]);
        AddRule(db, account, 10, "And", [new("senderDomain", "example.test"), new("hasAttachment", null)],
            [new("markRead"), new("stopProcessing")]);
        await db.SaveChangesAsync();
        var operations = new RecordingOperations();

        await new MailRuleEvaluator(db, operations, NullLogger<MailRuleEvaluator>.Instance)
            .EvaluatePendingAsync(account, folder, CancellationToken.None);

        Assert.Equal([MailOperationKind.Read], operations.Calls);
        Assert.False((await db.Mails.FindAsync(mail))!.RulePending);
    }

    [Fact]
    public async Task FailedAction_DoesNotBlockAnotherMessage_AndRemainsRetryable()
    {
        await using var db = CreateDb();
        var (account, folder, failedMail) = await SeedAsync(db);
        var goodMail = Guid.NewGuid();
        db.Mails.Add(new MailClient.Domain.Entities.Mail
        {
            Id = goodMail,
            MailAccountId = account,
            MailFolderId = folder,
            Uid = 2,
            FromAddress = "also@example.test",
            Subject = "offer",
            RulePending = true,
            ReceivedAt = DateTime.UtcNow,
            InternalDate = DateTime.UtcNow
        });
        AddRule(db, account, 1, "Or", [new("subjectContains", "offer")], [new("star")]);
        await db.SaveChangesAsync();
        var operations = new RecordingOperations(failedMail);

        await new MailRuleEvaluator(db, operations, NullLogger<MailRuleEvaluator>.Instance)
            .EvaluatePendingAsync(account, folder, CancellationToken.None);

        Assert.True((await db.Mails.FindAsync(failedMail))!.RulePending);
        Assert.False((await db.Mails.FindAsync(goodMail))!.RulePending);
        Assert.Equal(2, operations.Calls.Count);
    }

    [Fact]
    public async Task RulesWithoutFolderCondition_IgnoreSentMail_AndFolderConditionTargetsItsFolder()
    {
        await using var db = CreateDb();
        var (account, inbox, _) = await SeedAsync(db);
        var sent = Guid.NewGuid();
        var sentMail = Guid.NewGuid();
        db.MailFolders.Add(new MailFolder { Id = sent, MailAccountId = account, Name = "Sent", FullName = "Sent", FolderType = MailFolderType.Sent });
        db.Mails.Add(new MailClient.Domain.Entities.Mail
        {
            Id = sentMail,
            MailAccountId = account,
            MailFolderId = sent,
            Uid = 9,
            FromAddress = "news@example.test",
            Subject = "offer",
            RulePending = true,
            ReceivedAt = DateTime.UtcNow,
            InternalDate = DateTime.UtcNow
        });
        AddRule(db, account, 1, "And", [new("subjectContains", "offer")], [new("markRead")]);
        AddRule(db, account, 2, "And", [new("folder", sent.ToString().ToUpperInvariant())], [new("star")]);
        await db.SaveChangesAsync();
        var operations = new RecordingOperations();
        var evaluator = new MailRuleEvaluator(db, operations, NullLogger<MailRuleEvaluator>.Instance);

        await evaluator.EvaluatePendingAsync(account, sent, CancellationToken.None);
        await evaluator.EvaluatePendingAsync(account, inbox, CancellationToken.None);

        Assert.Equal([MailOperationKind.Star, MailOperationKind.Read], operations.Calls);
    }

    [Fact]
    public async Task PermanentActionFailure_IsNotRetriedForever()
    {
        await using var db = CreateDb();
        var (account, folder, mail) = await SeedAsync(db);
        AddRule(db, account, 1, "And", [new("subjectContains", "offer")], [new("move", FolderId: Guid.NewGuid())]);
        await db.SaveChangesAsync();

        await new MailRuleEvaluator(db, new RecordingOperations(mail, MailOperationError.FolderNotFound),
            NullLogger<MailRuleEvaluator>.Instance).EvaluatePendingAsync(account, folder, CancellationToken.None);

        Assert.False((await db.Mails.FindAsync(mail))!.RulePending);
    }

    [Fact]
    public void SenderDomainAndRecipient_MatchOnlyExactDomainAndRecipients()
    {
        var mail = new MailClient.Domain.Entities.Mail
        {
            FromAddress = "news@example.test",
            Participants =
            [new MailParticipant { Type = ParticipantType.Cc, Address = "person@sample.test" }]
        };
        Assert.True(MailRuleEvaluator.Matches(mail, new RuleCondition("senderDomain", "example.test")));
        Assert.False(MailRuleEvaluator.Matches(mail, new RuleCondition("senderDomain", "ample.test")));
        Assert.True(MailRuleEvaluator.Matches(mail, new RuleCondition("recipientContains", "PERSON@")));
    }

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static async Task<(Guid Account, Guid Folder, Guid Mail)> SeedAsync(AppDbContext db)
    {
        var account = Guid.NewGuid();
        var folder = Guid.NewGuid();
        var mail = Guid.NewGuid();
        db.MailAccounts.Add(new MailAccount
        {
            Id = account,
            EmailAddress = $"{account:N}@example.test",
            NormalizedEmailAddress = $"{account:N}@EXAMPLE.TEST"
        });
        db.MailFolders.Add(new MailFolder { Id = folder, MailAccountId = account, Name = "INBOX", FullName = "INBOX", FolderType = MailFolderType.Inbox });
        db.Mails.Add(new MailClient.Domain.Entities.Mail
        {
            Id = mail,
            MailAccountId = account,
            MailFolderId = folder,
            Uid = 1,
            FromAddress = "news@example.test",
            Subject = "offer",
            HasAttachments = true,
            RulePending = true,
            ReceivedAt = DateTime.UtcNow,
            InternalDate = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return (account, folder, mail);
    }

    private static void AddRule(AppDbContext db, Guid account, int priority, string logic,
        IReadOnlyList<RuleCondition> conditions, IReadOnlyList<RuleAction> actions)
    {
        db.MailRules.Add(new MailRule
        {
            Id = Guid.NewGuid(),
            MailAccountId = account,
            Name = "rule",
            Enabled = true,
            Priority = priority,
            Logic = logic,
            ConditionJson = JsonSerializer.Serialize(conditions, JsonOptions),
            ActionJson = JsonSerializer.Serialize(actions, JsonOptions),
            CreatedAt = DateTime.UtcNow
        });
    }

    private sealed class RecordingOperations(Guid? failingMail = null,
        MailOperationError failure = MailOperationError.ProviderUnavailable) : IMailOperationService
    {
        public List<MailOperationKind> Calls { get; } = [];
        public Task<MailOperationResult> ExecuteAsync(Guid accountId, MailOperationRequest request, string? correlationId, CancellationToken ct)
        {
            Calls.Add(request.Kind);
            return Task.FromResult(request.MailId == failingMail
                ? new MailOperationResult(false, failure)
                : new MailOperationResult(true));
        }

        public Task<BulkMailOperationResult> ExecuteBulkAsync(Guid accountId, IReadOnlyList<Guid> mailIds,
            MailOperationKind kind, Guid? destinationFolderId, string? correlationId, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
