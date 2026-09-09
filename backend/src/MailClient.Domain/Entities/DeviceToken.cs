namespace MailClient.Domain.Entities;

public class DeviceToken
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public DateTime RegisteredAt { get; set; }
}
