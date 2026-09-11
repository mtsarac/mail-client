namespace MailClient.Infrastructure.Push;

public sealed class FirebaseOptions
{
    public bool Enabled { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public string CredentialsPath { get; set; } = string.Empty;

    public void Validate()
    {
        if (!Enabled)
            return;
        if (string.IsNullOrWhiteSpace(ProjectId))
            throw new InvalidOperationException(
                "Firebase:ProjectId must be configured when Firebase:Enabled is true.");
        if (FirebaseCredentials.ResolvePath(CredentialsPath) is null)
            throw new InvalidOperationException(
                "Firebase credentials could not be resolved. Set Firebase:CredentialsPath or GOOGLE_APPLICATION_CREDENTIALS.");
    }
}
