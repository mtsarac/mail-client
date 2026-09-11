namespace MailClient.Infrastructure.Push;

public sealed class FirebaseOptions
{
    public bool Enabled { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public string CredentialsPath { get; set; } = string.Empty;

    public void Validate()
    {
        // Fail-fast: when enabled, actually load the credentials and
        // initialize the Firebase Admin SDK so configuration problems
        // surface at startup instead of as silent push outages.
        FirebaseSetup.EnsureInitialized(this);
    }
}
