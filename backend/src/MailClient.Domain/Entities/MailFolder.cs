using MailClient.Domain.Enums;

namespace MailClient.Domain.Entities;

public class MailFolder
{
    public Guid Id { get; set; }
    public Guid MailAccountId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public MailFolderType FolderType { get; set; } = MailFolderType.Unknown;
    public uint UidValidity { get; set; }
    public bool IsSyncEnabled { get; set; }
    public MailAccount? MailAccount { get; set; }
    public SyncState? SyncState { get; set; }
    public ICollection<Mail> Mails { get; set; } = new List<Mail>();
}
