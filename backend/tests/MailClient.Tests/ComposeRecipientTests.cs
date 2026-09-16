using MailClient.Application.Mail;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Email;
using MimeKit;

namespace MailClient.Tests;

public sealed class ComposeRecipientTests
{
    [Fact]
    public void Reply_UsesReplyTo_WhenPresent()
    {
        var mail = MailWithParticipants(
            (ParticipantType.From, "from@example.test"),
            (ParticipantType.ReplyTo, "reply@example.test"));

        var result = MailRecipientResolver.Reply(mail);

        Assert.Equal(["reply@example.test"], result.To.Select(x => x.Address));
    }

    [Fact]
    public void Reply_FallsBackToFrom()
    {
        var mail = MailWithParticipants((ParticipantType.From, "from@example.test"));

        var result = MailRecipientResolver.Reply(mail);

        Assert.Equal(["from@example.test"], result.To.Select(x => x.Address));
    }

    [Fact]
    public void ReplyAll_ExcludesAccount_Deduplicates_AndDoesNotExposeBcc()
    {
        var mail = MailWithParticipants(
            (ParticipantType.From, "me@example.test"),
            (ParticipantType.ReplyTo, "reply@example.test"),
            (ParticipantType.To, "me@example.test"),
            (ParticipantType.To, "to@example.test"),
            (ParticipantType.Cc, "reply@example.test"),
            (ParticipantType.Cc, "cc@example.test"),
            (ParticipantType.Bcc, "secret@example.test"));

        var result = MailRecipientResolver.ReplyAll(mail, "ME@example.test");

        Assert.Equal(["reply@example.test", "to@example.test"], result.To.Select(x => x.Address));
        Assert.Equal(["cc@example.test"], result.Cc.Select(x => x.Address));
        Assert.DoesNotContain(result.To.Concat(result.Cc), x => x.Address == "secret@example.test");
    }

    [Fact]
    public void MimeBuilder_WritesRecipientListsAndThreadingHeaders()
    {
        var message = MimeMessageBuilder.Build(
            "me@example.test",
            "Me",
            [new MailboxAddress("To", "to@example.test")],
            [new MailboxAddress("Cc", "cc@example.test")],
            [new MailboxAddress("Bcc", "bcc@example.test")],
            "Re: Hello",
            null,
            "body",
            [],
            "source@example.test",
            "<source@example.test> <prior@example.test>");

        Assert.Equal("to@example.test", message.To.Mailboxes.Single().Address);
        Assert.Equal("cc@example.test", message.Cc.Mailboxes.Single().Address);
        Assert.Equal("bcc@example.test", message.Bcc.Mailboxes.Single().Address);
        Assert.Equal("source@example.test", message.InReplyTo);
        Assert.Equal(["source@example.test", "prior@example.test"], message.References);
        Assert.False(string.IsNullOrWhiteSpace(message.MessageId));
    }

    private static Mail MailWithParticipants(params (ParticipantType Type, string Address)[] participants)
    {
        var mail = new Mail { Id = Guid.NewGuid(), Subject = "Hello", MessageId = "source@example.test" };
        mail.Participants = participants.Select((participant, index) => new MailParticipant
        {
            MailId = mail.Id,
            Type = participant.Type,
            Address = participant.Address,
            NormalizedAddress = participant.Address.ToUpperInvariant(),
            SortOrder = index
        }).ToList();
        return mail;
    }
}
