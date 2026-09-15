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
        string? to = null)
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
            SentAt = DateTime.UtcNow.AddMinutes(-db.Mails.Count()),
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
