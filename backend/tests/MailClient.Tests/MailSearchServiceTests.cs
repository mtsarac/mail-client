using MailClient.Application.Mail;
using MailClient.Application.Runtime;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Security;
using MailClient.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Tests;

public sealed class MailSearchServiceTests
{
    [Fact]
    public async Task Search_PendingMove_AppearsOnlyInDestinationWithPendingMarker()
    {
        await using var db = CreateDb();
        var accountId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var destinationId = Guid.NewGuid();
        var mail = Message(accountId, sourceId, 1);
        mail.ExpectedMailFolderId = destinationId;
        mail.ReconciliationState = MailReconciliationState.Pending;
        db.Mails.Add(mail);
        await db.SaveChangesAsync();
        var service = new MailSearchService(db, FixedRuntimeSettingsStore.Operation());
        var request = new MailSearchRequest(null, sourceId, null, null, null, null, null, null, null, null, 1, 20);

        var source = await service.SearchAsync(accountId, request, CancellationToken.None);
        var destination = await service.SearchAsync(accountId, request with { FolderId = destinationId }, CancellationToken.None);
        var anywhere = await service.SearchAsync(accountId, request with { FolderId = null }, CancellationToken.None);

        Assert.Equal(0, source.Total);
        var moved = Assert.Single(destination.Items);
        Assert.Equal(mail.Id, moved.Id);
        Assert.Equal(destinationId, moved.FolderId);
        Assert.True(moved.ReconciliationPending);
        Assert.Equal(mail.Id, Assert.Single(anywhere.Items).Id);
        Assert.Empty((await service.SearchAsync(Guid.NewGuid(), request with { FolderId = destinationId }, CancellationToken.None)).Items);
        Assert.Equal(sourceId, mail.MailFolderId);
    }

    [Theory]
    [InlineData("subject")]
    [InlineData("body")]
    [InlineData("senderAddress")]
    [InlineData("senderName")]
    [InlineData("to")]
    [InlineData("messageId")]
    [InlineData("participantAddress")]
    [InlineData("participantName")]
    [InlineData("attachment")]
    public async Task Search_MatchesLiteralSubstringInEverySearchableField(string field)
    {
        await using var db = CreateDb();
        var mail = Message(Guid.NewGuid(), Guid.NewGuid(), 1);
        switch (field)
        {
            case "subject": mail.Subject = "Domatesli tarif"; break;
            case "body":
                mail.BodyText = "Domatesli tarif";
                mail.BodyHtml = new MailContentCipher(new EphemeralDataProtectionProvider()).Protect("<p>Domatesli tarif</p>");
                break;
            case "senderAddress": mail.FromAddress = "domatesli@example.test"; break;
            case "senderName": mail.FromDisplayName = "Domatesli"; break;
            case "to": mail.ToAddress = "domatesli@example.test"; break;
            case "messageId": mail.MessageId = "<domatesli@example.test>"; break;
            case "participantAddress":
                db.Participants.Add(new MailParticipant { Id = Guid.NewGuid(), MailId = mail.Id, Type = ParticipantType.Cc, Address = "domatesli@example.test" });
                break;
            case "participantName":
                db.Participants.Add(new MailParticipant { Id = Guid.NewGuid(), MailId = mail.Id, Type = ParticipantType.ReplyTo, DisplayName = "Domatesli" });
                break;
            case "attachment":
                db.Attachments.Add(new Attachment { Id = Guid.NewGuid(), MailAccountId = mail.MailAccountId, MailId = mail.Id, FileName = "Domatesli.pdf", StoragePath = "test" });
                break;
        }
        db.Mails.Add(mail);
        db.Mails.Add(Message(mail.MailAccountId, mail.MailFolderId, 2));
        await db.SaveChangesAsync();
        var result = await Search(db, mail.MailAccountId, "  DOMATES  ");
        Assert.Equal(mail.Id, Assert.Single(result.Items).Id);
        Assert.Equal(1, result.Total);
    }

    [Fact]
    public async Task Search_RequiresEveryTermButAllowsTermsAcrossFields_AndMatchesWildcardsLiterally()
    {
        await using var db = CreateDb();
        var accountId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        var match = Message(accountId, folderId, 1);
        match.Subject = "Domatesli nefis";
        match.BodyText = "çorba 50%_indirim";
        var partial = Message(accountId, folderId, 2);
        partial.Subject = "Domatesli";
        var wildcardImpostor = Message(accountId, folderId, 3);
        wildcardImpostor.BodyText = "50anythingindirim";
        db.Mails.AddRange(match, partial, wildcardImpostor);
        await db.SaveChangesAsync();

        Assert.Equal(match.Id, Assert.Single((await Search(db, accountId, "domates \t ÇORBA")).Items).Id);
        Assert.Equal(match.Id, Assert.Single((await Search(db, accountId, "50%_indirim")).Items).Id);
        Assert.Empty((await Search(db, accountId, "domates missing")).Items);
    }

    [Fact]
    public async Task Search_FiltersBeforePagingAndNeverIncludesAnotherAccountOrForeignLabel()
    {
        await using var db = CreateDb();
        var accountId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        var labelId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var receivedAt = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
        for (uint uid = 1; uid <= 5; uid++)
        {
            var mail = Message(uid == 5 ? Guid.NewGuid() : accountId, uid == 4 ? Guid.NewGuid() : folderId, uid);
            mail.Subject = "Domatesli nefis çorba";
            mail.ReceivedAt = receivedAt.AddMinutes(uid);
            mail.FromDisplayName = "Chef";
            mail.ToAddress = "cook@example.test";
            mail.Flagged = true;
            mail.HasAttachments = true;
            mail.ConversationId = conversationId;
            mail.IsRead = uid == 3;
            db.Mails.Add(mail);
            db.MailLabelAssignments.Add(new MailLabelAssignment { Id = Guid.NewGuid(), MailAccountId = mail.MailAccountId, MailId = mail.Id, MailLabelId = labelId });
        }
        await db.SaveChangesAsync();
        var service = new MailSearchService(db, FixedRuntimeSettingsStore.Operation());
        var request = new MailSearchRequest("domates çorba", folderId, conversationId, "CHE", "COOK", receivedAt, receivedAt.AddDays(1), false, true, true, 1, 1, labelId);

        var first = await service.SearchAsync(accountId, request, CancellationToken.None);
        var second = await service.SearchAsync(accountId, request with { Page = 2 }, CancellationToken.None);
        Assert.Equal(2, first.Total);
        Assert.Equal(2u, await db.Mails.Where(mail => mail.Id == Assert.Single(first.Items).Id).Select(mail => mail.Uid).SingleAsync());
        Assert.Equal(1u, await db.Mails.Where(mail => mail.Id == Assert.Single(second.Items).Id).Select(mail => mail.Uid).SingleAsync());
        Assert.Empty((await service.SearchAsync(accountId, request with { LabelId = Guid.NewGuid() }, CancellationToken.None)).Items);
        Assert.Empty((await service.SearchAsync(accountId, request with { Page = 3 }, CancellationToken.None)).Items);
    }

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static Mail Message(Guid accountId, Guid folderId, uint uid) => new()
    {
        Id = Guid.NewGuid(),
        MailAccountId = accountId,
        MailFolderId = folderId,
        Uid = uid,
        Subject = "plain",
        BodyText = "ordinary",
        ReceivedAt = DateTime.UtcNow
    };

    private static Task<MailListResponse> Search(AppDbContext db, Guid accountId, string text) =>
        new MailSearchService(db, FixedRuntimeSettingsStore.Operation()).SearchAsync(accountId,
            new MailSearchRequest(text, null, null, null, null, null, null, null, null, null, 1, 20), CancellationToken.None);
}
