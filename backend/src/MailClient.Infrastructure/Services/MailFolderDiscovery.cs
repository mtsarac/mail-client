using MailClient.Domain.Enums;
using MailKit;

namespace MailClient.Infrastructure.Services;

public static class MailFolderDiscovery
{
    public static MailFolderClassification Classify(FolderAttributes attributes, string fullName)
    {
        var folderType = attributes switch
        {
            var value when value.HasFlag(FolderAttributes.Inbox) => MailFolderType.Inbox,
            var value when value.HasFlag(FolderAttributes.Sent) => MailFolderType.Sent,
            var value when value.HasFlag(FolderAttributes.Drafts) => MailFolderType.Drafts,
            var value when value.HasFlag(FolderAttributes.Trash) => MailFolderType.Trash,
            var value when value.HasFlag(FolderAttributes.Junk) => MailFolderType.Junk,
            var value when value.HasFlag(FolderAttributes.Archive) => MailFolderType.Archive,
            _ when string.Equals(fullName, "INBOX", StringComparison.OrdinalIgnoreCase) => MailFolderType.Inbox,
            _ when string.Equals(fullName, "Sent", StringComparison.OrdinalIgnoreCase) => MailFolderType.Sent,
            _ => MailFolderType.Custom
        };

        return new MailFolderClassification(folderType, folderType is MailFolderType.Inbox or MailFolderType.Sent);
    }
}

public sealed record MailFolderClassification(MailFolderType FolderType, bool IsSyncEnabled);
