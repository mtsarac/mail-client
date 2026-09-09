namespace MailClient.Domain.Entities;

public class Mail
{
    public Guid Id { get; set; }
    public uint Uid { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    public string FromDisplayName { get; set; } = string.Empty;
    public string ToAddress { get; set; } = string.Empty;
    public string BodyHtml { get; set; } = string.Empty;
    public string BodyText { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; }
    public bool IsRead { get; set; }
    public string MailboxId { get; set; } = "INBOX";
    public ICollection<Attachment> Attachments { get; set; } = new List<Attachment>();
}
