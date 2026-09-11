using System.Security.Cryptography;
using System.Text.Json;
using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;

namespace MailClient.Infrastructure.Push;

// Fail-fast Firebase initialization. When Firebase:Enabled is true this loads
// credentials and creates the FirebaseApp at startup, so missing/unreadable/
// malformed/invalid credentials and SDK initialization failures surface as a
// clear configuration exception instead of a silent runtime push outage.
// No network or test notification is sent: only local credential loading and
// SDK object construction. Never includes secrets in messages or logs.
public static class FirebaseSetup
{
    public const string AppName = "mail-client-push";

    public static void EnsureInitialized(FirebaseOptions options) =>
        EnsureInitialized(options, AppName);

    internal static void EnsureInitialized(FirebaseOptions options, string appName)
    {
        if (!options.Enabled)
            return;
        if (string.IsNullOrWhiteSpace(options.ProjectId))
            throw new InvalidOperationException(
                "Firebase:ProjectId must be configured when Firebase:Enabled is true.");

        var credential = LoadCredential(options.CredentialsPath);
        var appOptions = new AppOptions
        {
            Credential = credential,
            ProjectId = options.ProjectId.Trim()
        };

        try
        {
            ReplaceApp(appOptions, appName);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                "Firebase Admin SDK initialization failed. Check Firebase:ProjectId and credentials.", ex);
        }
    }

    private static void ReplaceApp(AppOptions appOptions, string appName)
    {
        FirebaseApp.GetInstance(appName)?.Delete();

        FirebaseApp.Create(appOptions, appName);
    }

    private static GoogleCredential LoadCredential(string configuredPath)
    {
        // 1. Explicitly configured path wins and must be usable.
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (!File.Exists(configuredPath))
                throw new InvalidOperationException(
                    $"Firebase credentials file was not found: {configuredPath}. Set Firebase:CredentialsPath to a readable service-account JSON file.");
            return LoadServiceAccountFile(configuredPath);
        }

        // 2. GOOGLE_APPLICATION_CREDENTIALS.
        var fromEnv = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            if (!File.Exists(fromEnv))
                throw new InvalidOperationException(
                    $"Firebase credentials file from GOOGLE_APPLICATION_CREDENTIALS was not found: {fromEnv}.");
            return LoadServiceAccountFile(fromEnv);
        }

        // 3. Well-known Application Default Credentials location.
        var wellKnown = WellKnownApplicationDefaultPath();
        if (wellKnown is not null)
            return LoadServiceAccountFile(wellKnown);

        // 4. Normal Google ADC behavior (gcloud, GCE/GKE metadata, etc.).
        try
        {
            return GoogleCredential.GetApplicationDefault();
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                "Firebase credentials could not be resolved. Set Firebase:CredentialsPath or GOOGLE_APPLICATION_CREDENTIALS.", ex);
        }
    }

    private static GoogleCredential LoadServiceAccountFile(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Firebase credentials file could not be read: {path}.", ex);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Firebase credentials file is not valid JSON: {path}.", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || (root.TryGetProperty("type", out var type)
                    && !string.Equals(type.GetString(), "service_account", StringComparison.Ordinal)))
                throw new InvalidOperationException(
                    $"Firebase credentials file is not a service-account key: {path}.");
            var email = root.TryGetProperty("client_email", out var emailElement) ? emailElement.GetString() : null;
            var key = root.TryGetProperty("private_key", out var keyElement) ? keyElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(key))
                throw new InvalidOperationException(
                    $"Firebase service-account file is missing required fields: {path}.");
            try
            {
                using var rsa = RSA.Create();
                rsa.ImportFromPem(key);
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException)
            {
                throw new InvalidOperationException(
                    $"Firebase service-account private key is invalid: {path}.", ex);
            }
        }

        try
        {
            return CredentialFactory.FromJson<ServiceAccountCredential>(json).ToGoogleCredential();
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Firebase credentials file could not be loaded: {path}.", ex);
        }
    }

    private static string? WellKnownApplicationDefaultPath()
    {
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
}
