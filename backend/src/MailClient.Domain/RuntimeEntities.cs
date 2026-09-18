namespace MailClient.Domain.Entities;

public sealed class RuntimeConfiguration
{
    public int Id { get; set; }
    public int SchemaVersion { get; set; } = 1;
    public int Version { get; set; } = 1;
    public string SettingsJson { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}

public sealed class OAuthStateNonce
{
    public Guid Id { get; set; }
    public string NonceHash { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
}

public sealed class AllowlistedEmail
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";
    public string NormalizedEmail { get; set; } = "";
    public DateTime AddedAt { get; set; }
}
