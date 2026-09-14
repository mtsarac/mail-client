using System.Text.Json;
using System.Text.Json.Nodes;

// Recursive secret redaction for logged JSON. Case-insensitive key match.
// Multipart bodies are summarized before reaching here, never raw.
namespace MailClient.Api.Logging;

public static class LogRedactor
{
    public const string Redacted = "[REDACTED]";

    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "password",
        "newPassword",
        "currentPassword",
        "accessToken",
        "refreshToken",
        "token",
        "pushToken",
        "authorization",
        "secret",
        "clientSecret",
        "apiKey",
        "encryptedPassword",
        "credential",
        "credentials",
        "certificatePassword",
        "privateKey"
    };

    public static JsonNode? Redact(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            var redacted = new JsonObject();
            foreach (var (key, value) in obj)
                redacted[key] = SecretKeys.Contains(key) ? Redacted : Redact(value);
            return redacted;
        }

        if (node is JsonArray array)
        {
            var redacted = new JsonArray();
            foreach (var item in array)
                redacted.Add(Redact(item));
            return redacted;
        }

        return node?.DeepClone();
    }

    public static JsonNode? ParseAndRedact(string body)
    {
        try
        {
            return Redact(JsonNode.Parse(body));
        }
        catch (JsonException)
        {
            return body;
        }
    }
}
