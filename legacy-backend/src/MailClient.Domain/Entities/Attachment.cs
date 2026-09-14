// Attachment metadata pointing at a file under the local storage root.
namespace MailClient.Domain.Entities;

public class Attachment
{
    public Guid Id { get; set; }
    public Guid MailId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string StoragePath { get; set; } = string.Empty;
    public bool IsInline { get; set; }
    public string ContentId { get; set; } = string.Empty;
    public Mail? Mail { get; set; }
}
