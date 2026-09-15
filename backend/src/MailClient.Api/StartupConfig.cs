namespace MailClient.Api;

public static class StartupConfig
{
    public static void Validate(string? connectionString, string? keyPath, bool isProduction)
    {
        if (!isProduction)
            return;
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("ConnectionStrings:Default must be provided in production.");
        if (string.IsNullOrWhiteSpace(keyPath))
            throw new InvalidOperationException("DataProtection:KeyPath must be provided in production.");
    }
}
