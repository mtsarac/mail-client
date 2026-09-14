namespace MailClient.Infrastructure.Push;

public sealed class FirebaseOptions
{
    public bool Enabled { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public string CredentialsPath { get; set; } = string.Empty;

    public void Validate()
    {
        FirebaseSetup.EnsureInitialized(this);
    }
}
