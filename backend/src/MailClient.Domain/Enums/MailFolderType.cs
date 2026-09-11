// Special-use folder classification mapped from IMAP attributes.
namespace MailClient.Domain.Enums;

public enum MailFolderType
{
    Inbox,
    Sent,
    Drafts,
    Trash,
    Junk,
    Archive,
    Custom,
    Unknown
}
