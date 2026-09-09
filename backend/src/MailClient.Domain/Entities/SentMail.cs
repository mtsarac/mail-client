namespace MailClient.Domain.Entities;

public class SentMail
{
    public Guid Id { get; set; }
    public string ToAddress { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string BodyHtml { get; set; } = string.Empty;
    public DateTime SentAt { get; set; }
    public bool HasAttachments { get; set; }
}
