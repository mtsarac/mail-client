using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MailClient.Infrastructure.Push;

// Service-account OAuth2 token source for FCM HTTP v1. Singleton-style and
// thread-safe: the token is cached until shortly before expiry and shared
// across all sends.
public sealed class ServiceAccountTokenProvider(FirebaseOptions options, HttpClient http) : IFirebaseAccessTokenProvider
{
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string MessagingScope = "https://www.googleapis.com/auth/firebase.messaging";

    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _expiresAt;

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_accessToken is not null && DateTimeOffset.UtcNow < _expiresAt)
            return _accessToken;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_accessToken is not null && DateTimeOffset.UtcNow < _expiresAt)
                return _accessToken;

            var path = FirebaseCredentials.ResolvePath(options.CredentialsPath)
                ?? throw new InvalidOperationException("Firebase credentials could not be resolved.");
            var serviceAccount = FirebaseCredentials.LoadServiceAccount(path);
            var assertion = BuildAssertion(serviceAccount);
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                    ["assertion"] = assertion
                })
            };
            using var response = await http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            _accessToken = document.RootElement.GetProperty("access_token").GetString()
                ?? throw new InvalidOperationException("OAuth token response did not contain an access token.");
            var expiresIn = document.RootElement.TryGetProperty("expires_in", out var expires)
                && expires.TryGetInt32(out var seconds) ? seconds : 3600;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn - 60));
            return _accessToken;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static string BuildAssertion(ServiceAccountCredentials serviceAccount)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = Base64Url(Encoding.ASCII.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
        var payload = Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            iss = serviceAccount.ClientEmail,
            scope = MessagingScope,
            aud = TokenEndpoint,
            iat = now,
            exp = now + 3600
        })));
        var bytes = Encoding.ASCII.GetBytes($"{header}.{payload}");
        using var rsa = RSA.Create();
        rsa.ImportFromPem(serviceAccount.PrivateKey);
        var signature = Base64Url(rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        return $"{header}.{payload}.{signature}";
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
