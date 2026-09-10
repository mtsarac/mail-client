namespace MailClient.Domain.Entities;

public class SyncState
{
    public Guid Id { get; set; }
    public Guid MailFolderId { get; set; }
    public uint UidValidity { get; set; }
    public uint LastUid { get; set; }
    // Next UID position from which the sparse scan must continue. A long (not
    // uint) so a fully scanned 32-bit UID space can be represented by the
    // one-past-end sentinel (uint.MaxValue + 1 = 4294967296); the IMAP search
    // point derived from it (cursor - 1) always stays inside uint range.
    public long NextUidScanStart { get; set; } = 1;
    public DateTime? LastNewMailSyncAt { get; set; }
    public DateTime? LastFlagSyncAt { get; set; }
    public MailFolder? MailFolder { get; set; }
}
