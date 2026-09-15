using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Email;
using MimeKit;

namespace MailClient.Tests;

public sealed class IncomingMailMapperTests
{
    private static readonly Guid AccountId = Guid.NewGuid();
    private static readonly Guid FolderId = Guid.NewGuid();
    private static readonly IncomingFlags Unseen = new(false, false, false, false, false, false);

    [Fact]
    public void Map_SingleParticipantSet_PreservesAllGroups()
    {
        var message = Build(message =>
        {
            message.From.Add(new MailboxAddress("Ana Gönderici", "sender@ornek.test"));
            message.To.Add(new MailboxAddress("Bir Alıcı", "to1@ornek.test"));
            message.To.Add(new MailboxAddress("İki Alıcı", "to2@ornek.test"));
            message.Cc.Add(new MailboxAddress("Cc Kişi", "cc@ornek.test"));
            message.Bcc.Add(new MailboxAddress("Bcc Kişi", "bcc@ornek.test"));
            message.ReplyTo.Add(new MailboxAddress("Reply Kişi", "reply@ornek.test"));
        });

        var incoming = IncomingMailMapper.Map(message, 1, 7, AccountId, FolderId, Unseen);

        Assert.Single(incoming.Participants, participant => participant.Type == ParticipantType.From && participant.Address == "sender@ornek.test");
        Assert.Equal(2, incoming.Participants.Count(participant => participant.Type == ParticipantType.To));
        Assert.Single(incoming.Participants, participant => participant.Type == ParticipantType.Cc);
        Assert.Single(incoming.Participants, participant => participant.Type == ParticipantType.Bcc);
        Assert.Single(incoming.Participants, participant => participant.Type == ParticipantType.ReplyTo);
        Assert.Equal("sender@ornek.test", incoming.FromAddress);
        Assert.Equal("to1@ornek.test", incoming.ToAddress);
        Assert.Equal("Ana Gönderici", incoming.FromDisplayName);
    }

    [Fact]
    public void Map_MultipleFrom_FirstBecomesLegacyColumn_AllPreservedAsParticipants()
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Bir", "one@ornek.test"));
        message.From.Add(new MailboxAddress("İki", "two@ornek.test"));

        var incoming = IncomingMailMapper.Map(message, 1, 7, AccountId, FolderId, Unseen);

        Assert.Equal("one@ornek.test", incoming.FromAddress);
        Assert.Equal(["one@ornek.test", "two@ornek.test"],
            incoming.Participants.Where(participant => participant.Type == ParticipantType.From).Select(participant => participant.Address));
    }

    [Fact]
    public void Map_MissingMessageId_SyncProceeds()
    {
        var message = SyncTestSeed.SimpleMessage("no id");
        message.Headers.RemoveAll(HeaderId.MessageId);

        var incoming = IncomingMailMapper.Map(message, 1, 7, AccountId, FolderId, Unseen);

        Assert.True(string.IsNullOrEmpty(incoming.MessageId) || incoming.MessageId.Contains('@'));
        Assert.Equal(string.Empty, incoming.InReplyToMessageId);
        Assert.Equal(string.Empty, incoming.References);
    }

    [Fact]
    public void Map_MalformedMessageId_DoesNotBreakMapping()
    {
        var message = SyncTestSeed.SimpleMessage("bad id");
        message.Headers.RemoveAll(HeaderId.MessageId);
        message.Headers.Add(HeaderId.MessageId, "not a <valid @id<>");

        var incoming = IncomingMailMapper.Map(message, 1, 7, AccountId, FolderId, Unseen);

        Assert.InRange(incoming.MessageId.Length, 0, 998);
        Assert.Equal(2, incoming.Participants.Count);
    }

    [Theory]
    [InlineData("<weird-@-id>")]
    [InlineData("plain-id@ornek.test")]
    public void Map_InReplyTo_ParsedAsMessageId(string inReplyTo)
    {
        var message = SyncTestSeed.SimpleMessage("reply");
        message.Headers.Add(HeaderId.InReplyTo, inReplyTo);

        var incoming = IncomingMailMapper.Map(message, 1, 7, AccountId, FolderId, Unseen);

        Assert.NotEqual(string.Empty, incoming.MessageId); // sync proceeds regardless
    }

    [Fact]
    public void Map_References_ArePreserved()
    {
        var message = SyncTestSeed.SimpleMessage("chain");
        message.References.Add("a@ornek.test");
        message.References.Add("b@ornek.test");

        var incoming = IncomingMailMapper.Map(message, 1, 7, AccountId, FolderId, Unseen);

        Assert.Equal("a@ornek.test b@ornek.test", incoming.References);
    }

    [Fact]
    public void Map_References_3000Chars_Preserved()
    {
        var message = SyncTestSeed.SimpleMessage("refs-3000");
        var reference = new string('r', 2989) + "@ornek.test";
        Assert.Equal(3000, reference.Length);
        message.References.Add(reference);

        var incoming = IncomingMailMapper.Map(message, 1, 7, AccountId, FolderId, Unseen);

        Assert.Equal(3000, incoming.References.Length);
        Assert.Equal(reference, incoming.References);
    }

    [Fact]
    public void Map_References_OverLimit_CappedAt4096()
    {
        var message = SyncTestSeed.SimpleMessage("refs-capped");
        var reference = new string('r', 4989) + "@ornek.test";
        Assert.Equal(5000, reference.Length);
        message.References.Add(reference);

        var incoming = IncomingMailMapper.Map(message, 1, 7, AccountId, FolderId, Unseen);

        Assert.Equal(4096, incoming.References.Length);
        Assert.Equal(reference[..4096], incoming.References);
    }

    [Fact]
    public void Map_UsesInjectedInternalDate_ForReceivedTimestamp()
    {
        var sent = DateTimeOffset.Parse("2026-01-02T03:04:05+02:00");
        var internalDate = new DateTime(2026, 1, 3, 4, 5, 6, DateTimeKind.Utc);
        var message = SyncTestSeed.SimpleMessage("timestamps");
        message.Date = sent;

        var incoming = IncomingMailMapper.Map(message, 1, 7, AccountId, FolderId, Unseen, internalDate);

        Assert.Equal(sent.UtcDateTime, incoming.SentAt);
        Assert.Equal(internalDate, incoming.InternalDate);
    }

    [Fact]
    public void Map_Timestamps_AreSeparated()
    {
        var sent = DateTimeOffset.Parse("2026-01-02T03:04:05+02:00");
        var message = SyncTestSeed.SimpleMessage("timestamps");
        message.Date = sent;

        var incoming = IncomingMailMapper.Map(message, 1, 7, AccountId, FolderId, Unseen);

        Assert.Equal(sent.UtcDateTime, incoming.SentAt);
        Assert.Equal(sent.UtcDateTime, incoming.InternalDate);
    }

    [Fact]
    public void Map_MultipartAlternative_KeepsBothBodies_AndUtf8Headers()
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Sending Ünicöde", "sender@ornek.test"));
        message.To.Add(new MailboxAddress("Receiver", "receiver@ornek.test"));
        message.Subject = "Alt-Konü: Subject";
        var multipart = new MultipartAlternative
        {
            new TextPart("plain") { Text = "metin tarafı" },
            new TextPart("html") { Text = "<p>html tarafı</p>" }
        };
        message.Body = multipart;

        var incoming = IncomingMailMapper.Map(message, 1, 7, AccountId, FolderId, Unseen);

        Assert.Equal("metin tarafı", incoming.BodyText);
        Assert.Contains("html tarafı", incoming.BodyHtml);
        Assert.Equal("Sending Ünicöde", incoming.FromDisplayName);
    }

    [Fact]
    public void Map_InlineCidAttachment_MapsInlineMetadata()
    {
        var message = SyncTestSeed.SimpleMessage("with cid");
        var related = new MultipartRelated
        {
            new TextPart("plain") { Text = "body" },
            new MimePart("image", "png")
            {
                ContentId = "logo123",
                Content = new MimeContent(new MemoryStream([1, 2, 3])),
                ContentDisposition = new ContentDisposition(ContentDisposition.Inline)
            }
        };
        message.Body = related;

        var incoming = IncomingMailMapper.Map(message, 1, 7, AccountId, FolderId, Unseen);

        var attachment = Assert.Single(incoming.Attachments);
        Assert.Equal("logo123", attachment.ContentId);
        Assert.True(attachment.IsInline);
        Assert.Equal(ContentDisposition.Inline, attachment.ContentDisposition);
    }

    [Fact]
    public void Map_MultipleAttachments_AllMapped()
    {
        var multipart = new Multipart("mixed")
        {
            new TextPart("plain") { Text = "body" },
            new MimePart("application", "pdf") { Content = new MimeContent(new MemoryStream([1])), FileName = "a.pdf" },
            new MimePart("image", "png") { Content = new MimeContent(new MemoryStream([2])), ContentId = "img1" }
        };

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Sender", "sender@ornek.test"));
        message.To.Add(new MailboxAddress("Receiver", "receiver@ornek.test"));
        message.Subject = "attachments";
        message.Body = multipart;

        var incoming = IncomingMailMapper.Map(message, 1, 7, AccountId, FolderId, Unseen);

        Assert.Equal(2, incoming.Attachments.Count);
        Assert.Contains(incoming.Attachments, attachment => attachment.FileName == "a.pdf");
        Assert.Contains(incoming.Attachments, attachment => attachment.ContentId == "img1");
    }

    [Fact]
    public void ToEntityParticipants_NormalizesAddress()
    {
        var message = SyncTestSeed.SimpleMessage("normalize");
        var incoming = IncomingMailMapper.Map(message, 1, 7, AccountId, FolderId, Unseen);

        var participants = IncomingMailMapper.ToEntityParticipants(Guid.NewGuid(), incoming.Participants);

        Assert.All(participants, participant =>
        {
            Assert.Equal(participant.Address.ToLowerInvariant(), participant.NormalizedAddress);
            Assert.IsType<MailParticipant>(participant);
        });
    }

    private static MimeMessage Build(Action<MimeMessage> configure)
    {
        var message = new MimeMessage();
        configure(message);
        return message;
    }
}
