namespace MailClient.Domain.Entities;

public class Mail
{
    public Guid Id { get; set; }
    public Guid MailAccountId { get; set; }
    public Guid MailFolderId { get; set; }
    public uint Uid { get; set; }
    public uint UidValidity { get; set; }
    public string MessageId { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    public string FromDisplayName { get; set; } = string.Empty;
    public string ToAddress { get; set; } = string.Empty;
    public string BodyHtml { get; set; } = string.Empty;
    public string BodyText { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; }
    public bool IsRead { get; set; }
    public bool HasAttachments { get; set; }
    public MailAccount? MailAccount { get; set; }
    public MailFolder? MailFolder { get; set; }
    public ICollection<Attachment> Attachments { get; set; } = new List<Attachment>();
}
