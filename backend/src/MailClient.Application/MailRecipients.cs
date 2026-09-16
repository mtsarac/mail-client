using MailClient.Domain.Enums;
using MailEntity = MailClient.Domain.Entities.Mail;
using MailParticipantEntity = MailClient.Domain.Entities.MailParticipant;
using MimeKit;

namespace MailClient.Application.Mail;

public sealed record MailRecipient(string Address, string DisplayName);

public sealed record ResolvedRecipients(
    IReadOnlyList<MailRecipient> To,
    IReadOnlyList<MailRecipient> Cc,
    IReadOnlyList<MailRecipient> Bcc)
{
    public static ResolvedRecipients Empty { get; } = new([], [], []);
}

public static class MailRecipientResolver
{
    public static ResolvedRecipients Parse(
        IEnumerable<string> to,
        IEnumerable<string> cc,
        IEnumerable<string> bcc)
    {
        var participants = new[]
        {
            (to, ParticipantType.To),
            (cc, ParticipantType.Cc),
            (bcc, ParticipantType.Bcc)
        };
        var parsed = participants.SelectMany(group => group.Item1.Select((address, index) => new MailParticipantEntity
        {
            Address = address,
            DisplayName = string.Empty,
            Type = group.Item2,
            SortOrder = index
        }));
        var result = Resolve(parsed.Where(x => x.Type == ParticipantType.To), parsed.Where(x => x.Type == ParticipantType.Cc), parsed.Where(x => x.Type == ParticipantType.Bcc), null);
        if (result.To.Count + result.Cc.Count + result.Bcc.Count == 0)
            throw new InvalidOperationException("recipient_required");
        return result;
    }

    public static ResolvedRecipients Reply(MailEntity mail) =>
        Resolve([.. ReplyParticipants(mail)], [], [], null);

    public static ResolvedRecipients ReplyAll(MailEntity mail, string accountAddress)
    {
        var own = accountAddress.Trim();
        var reply = ReplyParticipants(mail);
        var to = reply.Concat(Participants(mail, ParticipantType.To));
        var cc = Participants(mail, ParticipantType.Cc);
        return Resolve(to, cc, [], own);
    }

    private static IEnumerable<MailParticipantEntity> ReplyParticipants(MailEntity mail)
    {
        var replyTo = Participants(mail, ParticipantType.ReplyTo).ToList();
        return replyTo.Count > 0 ? replyTo : Participants(mail, ParticipantType.From);
    }

    private static IEnumerable<MailParticipantEntity> Participants(MailEntity mail, ParticipantType type) =>
        mail.Participants.Where(x => x.Type == type).OrderBy(x => x.SortOrder);

    private static ResolvedRecipients Resolve(
        IEnumerable<MailParticipantEntity> to,
        IEnumerable<MailParticipantEntity> cc,
        IEnumerable<MailParticipantEntity> bcc,
        string? accountAddress)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var own = accountAddress?.Trim();
        static MailRecipient Parse(MailParticipantEntity participant) =>
            MailboxAddress.TryParse(participant.Address, out var mailbox) && !string.IsNullOrWhiteSpace(mailbox.Address) && mailbox.Address.Contains('@', StringComparison.Ordinal)
                ? new(mailbox.Address, string.IsNullOrWhiteSpace(mailbox.Name) ? participant.DisplayName : mailbox.Name)
                : throw new InvalidOperationException("invalid_recipient");
        static IEnumerable<MailRecipient> Filter(IEnumerable<MailParticipantEntity> source, HashSet<string> seen, string? own)
        {
            foreach (var participant in source)
            {
                var recipient = Parse(participant);
                if (string.IsNullOrWhiteSpace(recipient.Address) || string.Equals(recipient.Address, own, StringComparison.OrdinalIgnoreCase) || !seen.Add(recipient.Address))
                    continue;
                yield return recipient;
            }
        }

        return new(Filter(to, seen, own).ToList(), Filter(cc, seen, own).ToList(), Filter(bcc, seen, own).ToList());
    }
}
