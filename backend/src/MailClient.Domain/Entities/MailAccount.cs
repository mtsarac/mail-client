using MailClient.Domain.Enums;

// User-owned IMAP/SMTP mailbox configuration with an encrypted password.
namespace MailClient.Domain.Entities;

public class MailAccount
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string EmailAddress { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string EncryptedPassword { get; set; } = string.Empty;
    public string ImapHost { get; set; } = string.Empty;
    public int ImapPort { get; set; }
    public MailSecurity ImapSecurity { get; set; } = MailSecurity.SslOnConnect;
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; }
    public MailSecurity SmtpSecurity { get; set; } = MailSecurity.StartTls;
    public bool SaveSentCopy { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public User? User { get; set; }
    public ICollection<MailFolder> Folders { get; set; } = new List<MailFolder>();
    public ICollection<Mail> Mails { get; set; } = new List<Mail>();
}
