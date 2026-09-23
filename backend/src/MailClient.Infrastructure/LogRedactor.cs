using System.Text.Json.Nodes;

namespace MailClient.Infrastructure.Observability;

public static class LogRedactor
{
    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "appSpecificPassword", "newPassword", "currentPassword", "accessToken", "refreshToken", "providerRefreshToken",
        "authorizationCode", "codeVerifier", "clientSecret", "token", "authorization", "credential", "secret", "encryptedMaterial", "apiKey"
    };

    public static string Redact(string json)
    {
        var node = JsonNode.Parse(json);
        RedactNode(node);
        return node?.ToJsonString() ?? "null";
    }

    private static void RedactNode(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            // "code" is also the stable error-code field of problem responses, so it is only a secret in the
            // OAuth authorization-response shape { state, code }; the state carries the protected PKCE verifier.
            var isOAuthAuthorizationResponse = obj.ContainsKey("state") && obj.ContainsKey("code");
            foreach (var key in obj.Select(item => item.Key).ToArray())
            {
                if (SecretKeys.Contains(key) || isOAuthAuthorizationResponse && key is "code" or "state")
                    obj[key] = "[REDACTED]";
                else RedactNode(obj[key]);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array) RedactNode(item);
        }
    }
}
