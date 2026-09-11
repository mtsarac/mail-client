using System.Text.Json;

namespace MailClient.Infrastructure.Push;

public sealed record ServiceAccountCredentials(string ClientEmail, string PrivateKey, string? ProjectId);

// Resolves Google service-account credentials without the FirebaseAdmin
// package (unavailable offline): explicit CredentialsPath first, then
// GOOGLE_APPLICATION_CREDENTIALS, then the well-known ADC location.
public static class FirebaseCredentials
{
    public static string? ResolvePath(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return configuredPath;
        var fromEnv = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
            return fromEnv;
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "gcloud", "application_default_credentials.json");
        if (File.Exists(appData))
            return appData;
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var unixWellKnown = Path.Combine(
            string.IsNullOrWhiteSpace(configHome)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
                : configHome,
            "gcloud", "application_default_credentials.json");
        return File.Exists(unixWellKnown) ? unixWellKnown : null;
    }

    public static ServiceAccountCredentials LoadServiceAccount(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var email = root.GetProperty("client_email").GetString();
            var key = root.GetProperty("private_key").GetString();
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(key))
                throw new InvalidOperationException("Firebase service-account file is missing required fields.");
            var projectId = root.TryGetProperty("project_id", out var project) ? project.GetString() : null;
            return new ServiceAccountCredentials(email, key, projectId);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException("Firebase service-account file could not be read.", ex);
        }
    }
}
