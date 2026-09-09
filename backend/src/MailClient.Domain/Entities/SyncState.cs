namespace MailClient.Domain.Entities;

public class SyncState
{
    public Guid Id { get; set; }
    public Guid MailFolderId { get; set; }
    public uint UidValidity { get; set; }
    public uint LastUid { get; set; }
    public DateTime? LastNewMailSyncAt { get; set; }
    public DateTime? LastFlagSyncAt { get; set; }
    public MailFolder? MailFolder { get; set; }
}
