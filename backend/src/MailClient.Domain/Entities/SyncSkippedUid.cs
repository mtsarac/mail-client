namespace MailClient.Domain.Entities;

public class SyncSkippedUid
{
    public Guid Id { get; set; }
    public Guid MailFolderId { get; set; }
    public uint Uid { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTime SkippedAt { get; set; }
    public MailFolder? MailFolder { get; set; }
}
