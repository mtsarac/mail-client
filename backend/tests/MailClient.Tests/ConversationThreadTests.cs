using MailClient.Application.Conversations;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using MailEntity = MailClient.Domain.Entities.Mail;

namespace MailClient.Tests;

public sealed class ConversationThreadTests
{
    [Theory]
    [InlineData("Re: Yıllık Rapor", "Yıllık Rapor")]
    [InlineData("FW: Mail: özet", "Mail: özet")]
    [InlineData("RE: FWD: SV: TR: zincir", "zincir")]
    [InlineData("Vv: küçük harf", "küçük harf")]
    [InlineData("  Yıllık   Rapor  ", "Yıllık Rapor")]
    [InlineData("Sorun: gündüz", "Sorun: gündüz")]
    public void NormalizeSubject_RemovesPrefixes_AndCollapsesWhitespace(string input, string expected)
    {
        Assert.Equal(expected, ConversationEngine.NormalizeSubject(input));
    }

    [Theory]
    [InlineData("  <abc@ornek.test>  ", "abc@ornek.test")]
    [InlineData("bare@ornek.test", "bare@ornek.test")]
    [InlineData(null, "")]
    public void NormalizeMessageId_StripsAngleBrackets_Idempotent(string? input, string expected)
    {
        Assert.Equal(expected, ConversationEngine.NormalizeMessageId(input));
        Assert.Equal(expected, ConversationEngine.NormalizeMessageId(expected));
    }

    [Fact]
    public void ParseReferences_SplitsOnWhitespaceAndCommas_Deduplicates()
    {
        var references = ConversationEngine.ParseReferences("<a@x> <b@x> a@x\t<c@x>");

        Assert.Equal(["a@x", "b@x", "c@x"], references);
    }

    [Fact]
    public async Task InReplyTo_GroupsReplyWithParent_RegardlessOfArrivalOrder()
    {
        await using var db = CreateDb();
        var (accountId, service) = await SeedAsync(db);
        var reply = await AddMailAsync(db, accountId, "Re: Plan", "me@client.test", messageId: "2@x", inReplyTo: "1@x");
        await service.AssignAsync(reply.Id, CancellationToken.None);

        var parent = await AddMailAsync(db, accountId, "Plan", "boss@corp.test", messageId: "1@x");
        await service.AssignAsync(parent.Id, CancellationToken.None);

        var replyAfter = await db.Mails.AsNoTracking().SingleAsync(item => item.Id == reply.Id);
        var parentAfter = await db.Mails.AsNoTracking().SingleAsync(item => item.Id == parent.Id);
        Assert.NotNull(replyAfter.ConversationId);
        Assert.Equal(replyAfter.ConversationId, parentAfter.ConversationId);
    }

    [Fact]
    public async Task ReferencesChain_MatchesAcrossIntermediateMessages()
    {
        await using var db = CreateDb();
        var (accountId, service) = await SeedAsync(db);
        var root = await AddMailAsync(db, accountId, "hiçbir konu", "boss@corp.test", messageId: "root@x");
        await service.AssignAsync(root.Id, CancellationToken.None);

        var deep = await AddMailAsync(db, accountId, "Fw: soru", "x@corp.test", messageId: "deep@x", references: "a@x b@x root@x");
        await service.AssignAsync(deep.Id, CancellationToken.None);

        var (rootAfter, deepAfter) = await LoadTwoAsync(db, root.Id, deep.Id);
        Assert.Equal(rootAfter.ConversationId, deepAfter.ConversationId);
    }

    [Fact]
    public async Task SubjectOnlyFallback_RequiresSharedParticipant()
    {
        await using var db = CreateDb();
        var (accountId, service) = await SeedAsync(db);
        var first = await AddMailAsync(db, accountId, "Fatura sorusu", "alice@ornek.test", to: "support@servis.test");
        await service.AssignAsync(first.Id, CancellationToken.None);

        var stranger = await AddMailAsync(db, accountId, "Re: Fwd: Fatura sorusu", "mallory@farkli.test", to: "completely-other@farkli.test");
        await service.AssignAsync(stranger.Id, CancellationToken.None);

        var shared = await AddMailAsync(db, accountId, "FWD: Fatura sorusu", "bob@ornek.test", to: "alice@ornek.test");
        await service.AssignAsync(shared.Id, CancellationToken.None);

        var firstAfter = await db.Mails.AsNoTracking().SingleAsync(item => item.Id == first.Id);
        var strangerAfter = await db.Mails.AsNoTracking().SingleAsync(item => item.Id == stranger.Id);
        var sharedAfter = await db.Mails.AsNoTracking().SingleAsync(item => item.Id == shared.Id);
        Assert.NotEqual(strangerAfter.ConversationId, firstAfter.ConversationId);
        Assert.Equal(firstAfter.ConversationId, sharedAfter.ConversationId);
    }

    [Fact]
    public async Task Reassigning_IsIdempotent()
    {
        await using var db = CreateDb();
        var (accountId, service) = await SeedAsync(db);
        var root = await AddMailAsync(db, accountId, "thread", "boss@corp.test", messageId: "root@x");
        await service.AssignAsync(root.Id, CancellationToken.None);
        var firstConversationId = (await db.Mails.AsNoTracking().SingleAsync(item => item.Id == root.Id)).ConversationId;

        await service.AssignAsync(root.Id, CancellationToken.None);

        var conversations = await db.Conversations.AsNoTracking().ToListAsync();
        var mail = await db.Mails.AsNoTracking().SingleAsync(item => item.Id == root.Id);
        Assert.Single(conversations, conversation => conversation.Id == firstConversationId!.Value);
        Assert.Equal(firstConversationId, mail.ConversationId);
    }

    [Fact]
    public async Task LateAncestor_MergesTwoSeparateConversations_WithDeterministicSurvivorAndBounds()
    {
        await using var db = CreateDb();
        var (accountId, service) = await SeedAsync(db);
        var sentA = DateTime.UtcNow.AddHours(-3);
        var sentB = DateTime.UtcNow.AddHours(-2);
        var sentBridge = DateTime.UtcNow.AddHours(-1);

        var mailA = await AddMailAsync(db, accountId, "Alpha plan", "alice@ornek.test", messageId: "a-t05-merge@x", sentAt: sentA);
        await service.AssignAsync(mailA.Id, CancellationToken.None);
        var mailB = await AddMailAsync(db, accountId, "Beta plan", "bob@ornek.test", messageId: "b-t05-merge@x", sentAt: sentB);
        await service.AssignAsync(mailB.Id, CancellationToken.None);

        var convA = (await db.Mails.AsNoTracking().SingleAsync(item => item.Id == mailA.Id)).ConversationId;
        var convB = (await db.Mails.AsNoTracking().SingleAsync(item => item.Id == mailB.Id)).ConversationId;
        Assert.NotNull(convA);
        Assert.NotNull(convB);
        Assert.NotEqual(convA, convB);

        var bridge = await AddMailAsync(db, accountId, "Re: Alpha plan", "carol@ornek.test",
            messageId: "c-t05-merge@x", inReplyTo: "a-t05-merge@x", references: "a-t05-merge@x b-t05-merge@x", sentAt: sentBridge);
        await service.AssignAsync(bridge.Id, CancellationToken.None);

        var accountConvs = await db.Conversations.AsNoTracking().Where(item => item.MailAccountId == accountId).ToListAsync();
        Assert.Single(accountConvs);
        Assert.Equal(convA, accountConvs[0].Id);

        var mails = await db.Mails.AsNoTracking().Where(item => item.MailAccountId == accountId).ToListAsync();
        Assert.Equal(3, mails.Count);
        Assert.All(mails, item => Assert.Equal(accountConvs[0].Id, item.ConversationId));
        Assert.Equal(sentA, accountConvs[0].StartedAt);
        Assert.Equal(sentBridge, accountConvs[0].LastMessageAt);
    }

    [Fact]
    public async Task LateAncestor_ByMessageId_RebindsChildren_AndIsIdempotent()
    {
        await using var db = CreateDb();
        var (accountId, service) = await SeedAsync(db);
        var sentRoot = DateTime.UtcNow.AddHours(-4);
        var sentFirst = DateTime.UtcNow.AddHours(-3);
        var sentSecond = DateTime.UtcNow.AddHours(-2);

        var first = await AddMailAsync(db, accountId, "Alpha item", "alice@ornek.test",
            messageId: "m1-t05-root@x", inReplyTo: "root-t05-late@x", sentAt: sentFirst);
        await service.AssignAsync(first.Id, CancellationToken.None);
        var second = await AddMailAsync(db, accountId, "Beta item", "bob@ornek.test",
            messageId: "m2-t05-root@x", inReplyTo: "root-t05-late@x", sentAt: sentSecond);
        await service.AssignAsync(second.Id, CancellationToken.None);

        var convFirst = (await db.Mails.AsNoTracking().SingleAsync(item => item.Id == first.Id)).ConversationId;
        var convSecond = (await db.Mails.AsNoTracking().SingleAsync(item => item.Id == second.Id)).ConversationId;
        Assert.NotNull(convFirst);
        Assert.NotNull(convSecond);
        Assert.NotEqual(convFirst, convSecond);

        var root = await AddMailAsync(db, accountId, "Root item", "boss@corp.test",
            messageId: "root-t05-late@x", sentAt: sentRoot);
        await service.AssignAsync(root.Id, CancellationToken.None);

        var survivor = await db.Conversations.AsNoTracking().Where(item => item.MailAccountId == accountId).ToListAsync();
        Assert.Single(survivor);
        Assert.Equal(convFirst, survivor[0].Id);
        var boundMails = await db.Mails.AsNoTracking().Where(item => item.MailAccountId == accountId).ToListAsync();
        Assert.All(boundMails, item => Assert.Equal(survivor[0].Id, item.ConversationId));
        Assert.Equal(sentRoot, survivor[0].StartedAt);
        Assert.Equal(sentSecond, survivor[0].LastMessageAt);

        await service.AssignAsync(root.Id, CancellationToken.None);
        await service.AssignAsync(first.Id, CancellationToken.None);

        var after = await db.Conversations.AsNoTracking().Where(item => item.MailAccountId == accountId).ToListAsync();
        Assert.Single(after);
        Assert.Equal(survivor[0].Id, after[0].Id);
        Assert.Equal(sentRoot, after[0].StartedAt);
        Assert.Equal(sentSecond, after[0].LastMessageAt);
    }

    [Fact]
    public async Task LateAncestor_DoesNotMergeAcrossAccounts()
    {
        await using var db = CreateDb();
        var (accountId, service) = await SeedAsync(db);
        var otherAccountId = Guid.NewGuid();
        db.MailAccounts.Add(new MailAccount
        {
            Id = otherAccountId,
            EmailAddress = "other@client.test",
            NormalizedEmailAddress = "other@client.test",
            Username = "other",
            ImapHost = "imap.example.test",
            ImapPort = 993,
            SmtpHost = "smtp.example.test",
            SmtpPort = 465,
            Status = MailAccountStatus.Active
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var otherService = new ConversationService(db);

        var mailA = await AddMailAsync(db, accountId, "Alpha plan", "alice@ornek.test", messageId: "a-t05-iso@x");
        await service.AssignAsync(mailA.Id, CancellationToken.None);
        var mailB = await AddMailAsync(db, accountId, "Beta plan", "bob@ornek.test", messageId: "b-t05-iso@x");
        await service.AssignAsync(mailB.Id, CancellationToken.None);
        var foreign = await AddMailAsync(db, otherAccountId, "Foreign plan", "carol@ornek.test",
            messageId: "a-t05-iso@x", references: "b-t05-iso@x");
        await otherService.AssignAsync(foreign.Id, CancellationToken.None);
        var foreignConv = (await db.Mails.AsNoTracking().SingleAsync(item => item.Id == foreign.Id)).ConversationId;

        var bridge = await AddMailAsync(db, accountId, "Re: Alpha plan", "dave@ornek.test",
            messageId: "c-t05-iso@x", inReplyTo: "a-t05-iso@x", references: "a-t05-iso@x b-t05-iso@x");
        await service.AssignAsync(bridge.Id, CancellationToken.None);

        var ownConvs = await db.Conversations.AsNoTracking().Where(item => item.MailAccountId == accountId).ToListAsync();
        Assert.Single(ownConvs);
        var foreignAfter = await db.Mails.AsNoTracking().SingleAsync(item => item.Id == foreign.Id);
        Assert.Equal(foreignConv, foreignAfter.ConversationId);
        var foreignConvs = await db.Conversations.AsNoTracking().Where(item => item.MailAccountId == otherAccountId).ToListAsync();
        Assert.Single(foreignConvs);
    }

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static async Task<(Guid AccountId, ConversationService Service)> SeedAsync(AppDbContext db)
    {
        var accountId = Guid.NewGuid();
        db.MailAccounts.Add(new MailAccount
        {
            Id = accountId,
            EmailAddress = "owner@client.test",
            NormalizedEmailAddress = "owner@client.test",
            Username = "owner",
            ImapHost = "imap.example.test",
            ImapPort = 993,
            SmtpHost = "smtp.example.test",
            SmtpPort = 465,
            Status = MailAccountStatus.Active
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (accountId, new ConversationService(db));
    }

    private static async Task<MailEntity> AddMailAsync(
        AppDbContext db,
        Guid accountId,
        string subject,
        string from,
        string? messageId = null,
        string? inReplyTo = null,
        string? references = null,
        string? to = null,
        DateTime? sentAt = null)
    {
        var mail = new MailEntity
        {
            Id = Guid.NewGuid(),
            MailAccountId = accountId,
            MailFolderId = Guid.NewGuid(),
            Uid = (uint)Random.Shared.Next(1, int.MaxValue),
            UidValidity = 7,
            Subject = subject,
            MessageId = messageId ?? "",
            InReplyToMessageId = inReplyTo ?? "",
            References = references ?? "",
            SentAt = sentAt ?? DateTime.UtcNow.AddMinutes(-db.Mails.Count()),
            ReceivedAt = DateTime.UtcNow,
            InternalDate = DateTime.UtcNow
        };
        mail.Participants.Add(new MailParticipant
        {
            Id = Guid.NewGuid(),
            MailId = mail.Id,
            Type = ParticipantType.From,
            Address = from,
            NormalizedAddress = from.ToLowerInvariant(),
            DisplayName = from
        });
        if (to is { Length: > 0 })
            mail.Participants.Add(new MailParticipant
            {
                Id = Guid.NewGuid(),
                MailId = mail.Id,
                Type = ParticipantType.To,
                Address = to,
                NormalizedAddress = to.ToLowerInvariant()
            });
        db.Mails.Add(mail);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return mail;
    }

    private static async Task<(MailEntity First, MailEntity Second)> LoadTwoAsync(AppDbContext db, Guid firstId, Guid secondId)
    {
        var first = await db.Mails.AsNoTracking().SingleAsync(item => item.Id == firstId);
        var second = await db.Mails.AsNoTracking().SingleAsync(item => item.Id == secondId);
        return (first, second);
    }
}
