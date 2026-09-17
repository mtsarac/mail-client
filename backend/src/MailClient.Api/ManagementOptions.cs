namespace MailClient.Api;

public sealed class ManagementOptions
{
    public bool Enabled { get; init; }
    public string ApiKey { get; init; } = "";

    public void Validate(bool isProduction)
    {
        if (Enabled && isProduction && string.IsNullOrWhiteSpace(ApiKey))
            throw new InvalidOperationException("Management:ApiKey must be provided when management API is enabled.");
    }
}
