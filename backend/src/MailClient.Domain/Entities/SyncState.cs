namespace MailClient.Domain.Entities;

public class SyncState
{
    public Guid Id { get; set; }
    public string MailboxId { get; set; } = "INBOX";
    public uint LastUid { get; set; }
}
