using System.Net.Http.Json;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using MailClient.Domain.Enums;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.Extensions;

namespace MailClient.Infrastructure.OAuth;

public sealed class OAuthOptions
{
    public OAuthProviderOptions Google { get; init; } = new();
    public OAuthProviderOptions Microsoft { get; init; } = new();
    public int StateLifetimeMinutes { get; init; } = 10;
}

public sealed class OAuthProviderOptions
{
    public string ClientId { get; init; } = "";
    public string ClientSecret { get; init; } = "";
    public string Tenant { get; init; } = "organizations";
    public string[] RedirectUris { get; init; } = [];

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret)
        && RedirectUris.Length > 0;
    public bool AllowsRedirectUri(string redirectUri) => RedirectUris.Contains(redirectUri, StringComparer.Ordinal);
}

public sealed record OAuthToken(string AccessToken, string? RefreshToken, DateTime ExpiresAt, string Scopes);
public sealed record OAuthStatePayload(MailProvider Provider, string Email, string RedirectUri, string CodeVerifier, string? DeviceIdentifier, string Nonce);

public interface IOAuthProvider
{
    MailProvider Provider { get; }
    bool IsConfigured { get; }
    string PrimaryRedirectUri { get; }
    string CreateAuthorizationUrl(string email, string redirectUri, string state, string codeChallenge);
    Task<OAuthToken> ExchangeCodeAsync(string code, string codeVerifier, string redirectUri, CancellationToken cancellationToken);
    Task<OAuthToken> RefreshAsync(string refreshToken, CancellationToken cancellationToken);
}

public abstract class OAuthProvider(HttpClient httpClient, OAuthProviderOptions options) : IOAuthProvider
{
    protected OAuthProviderOptions Options { get; } = options;
    protected HttpClient HttpClient { get; } = httpClient;
    public abstract MailProvider Provider { get; }
    public bool IsConfigured => Options.IsConfigured;
    public string PrimaryRedirectUri => Options.RedirectUris.Length > 0 ? Options.RedirectUris[0] : throw new InvalidOperationException("oauth_provider_not_configured");
    protected abstract string AuthorizationEndpoint { get; }
    protected abstract string TokenEndpoint { get; }
    protected abstract string Scope { get; }

    public string CreateAuthorizationUrl(string email, string redirectUri, string state, string codeChallenge)
    {
        EnsureConfiguredAndRedirectUri(redirectUri);
        var values = new Dictionary<string, string>
        {
            ["client_id"] = Options.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["scope"] = Scope,
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["login_hint"] = email
        };
        AddAuthorizationParameters(values);
        return BuildUrl(AuthorizationEndpoint, values);
    }

    public Task<OAuthToken> ExchangeCodeAsync(string code, string codeVerifier, string redirectUri, CancellationToken cancellationToken)
    {
        EnsureConfiguredAndRedirectUri(redirectUri);
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(codeVerifier))
            throw new InvalidOperationException("oauth_code_exchange_failed");
        return RequestTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = Options.ClientId,
            ["client_secret"] = Options.ClientSecret,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
            ["redirect_uri"] = redirectUri
        }, cancellationToken);
    }

    public Task<OAuthToken> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new InvalidOperationException("mail_account_needs_reauthentication");
        return RequestTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = Options.ClientId,
            ["client_secret"] = Options.ClientSecret,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = Scope
        }, cancellationToken);
    }

    protected virtual void AddAuthorizationParameters(Dictionary<string, string> values) { }

    private async Task<OAuthToken> RequestTokenAsync(Dictionary<string, string> values, CancellationToken cancellationToken)
    {
        using var response = await HttpClient.PostAsync(TokenEndpoint, new FormUrlEncodedContent(values), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadFromJsonAsync<OAuthError>(cancellationToken: cancellationToken);
            if (body?.Error is "invalid_grant" or "invalid_client" or "unauthorized_client")
                throw new InvalidOperationException("mail_account_needs_reauthentication");
            throw new InvalidOperationException("oauth_code_exchange_failed");
        }
        var token = await response.Content.ReadFromJsonAsync<OAuthTokenResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("oauth_code_exchange_failed");
        if (string.IsNullOrWhiteSpace(token.AccessToken))
            throw new InvalidOperationException("oauth_code_exchange_failed");
        return new OAuthToken(token.AccessToken, token.RefreshToken, DateTime.UtcNow.AddSeconds(Math.Max(token.ExpiresIn, 1)), token.Scope ?? Scope);
    }

    private void EnsureConfiguredAndRedirectUri(string redirectUri)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("oauth_provider_not_configured");
        if (!Options.AllowsRedirectUri(redirectUri))
            throw new InvalidOperationException("oauth_redirect_uri_invalid");
    }

    private static string BuildUrl(string endpoint, Dictionary<string, string> values) =>
        $"{endpoint}?{string.Join('&', values.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"))}";

    private sealed record OAuthTokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("scope")] string? Scope);
    private sealed record OAuthError([property: JsonPropertyName("error")] string? Error);
}

public sealed class GoogleOAuthProvider(HttpClient httpClient, OAuthProviderOptions options) : OAuthProvider(httpClient, options)
{
    public override MailProvider Provider => MailProvider.Google;
    protected override string AuthorizationEndpoint => "https://accounts.google.com/o/oauth2/v2/auth";
    protected override string TokenEndpoint => "https://oauth2.googleapis.com/token";
    protected override string Scope => "https://mail.google.com/";
    protected override void AddAuthorizationParameters(Dictionary<string, string> values)
    {
        values["access_type"] = "offline";
        values["prompt"] = "consent";
    }
}

public sealed class MicrosoftOAuthProvider(HttpClient httpClient, OAuthProviderOptions options) : OAuthProvider(httpClient, options)
{
    public override MailProvider Provider => MailProvider.Microsoft;
    protected override string AuthorizationEndpoint => $"https://login.microsoftonline.com/{Uri.EscapeDataString(Options.Tenant)}/oauth2/v2.0/authorize";
    protected override string TokenEndpoint => $"https://login.microsoftonline.com/{Uri.EscapeDataString(Options.Tenant)}/oauth2/v2.0/token";
    protected override string Scope => "https://outlook.office.com/IMAP.AccessAsUser.All https://outlook.office.com/SMTP.Send offline_access";
}

public sealed class OAuthStateProtector(IDataProtectionProvider provider, TimeSpan lifetime)
{
    private readonly ITimeLimitedDataProtector _protector = provider.CreateProtector("MailClient.OAuthState.v1").ToTimeLimitedDataProtector();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _consumedNonces = new(StringComparer.Ordinal);

    public string Protect(OAuthStatePayload payload) => _protector.Protect(System.Text.Json.JsonSerializer.Serialize(payload), lifetime);

    public OAuthStatePayload Consume(string protectedState)
    {
        try
        {
            var payload = System.Text.Json.JsonSerializer.Deserialize<OAuthStatePayload>(_protector.Unprotect(protectedState, out var expiration))
                ?? throw new InvalidOperationException("oauth_state_invalid");
            foreach (var consumed in _consumedNonces.Where(item => item.Value <= DateTimeOffset.UtcNow))
                _consumedNonces.TryRemove(consumed.Key, out _);
            if (string.IsNullOrWhiteSpace(payload.Nonce) || !_consumedNonces.TryAdd(payload.Nonce, expiration))
                throw new InvalidOperationException("oauth_state_invalid");
            return payload;
        }
        catch (Exception ex) when (ex is CryptographicException or System.Text.Json.JsonException)
        {
            throw new InvalidOperationException("oauth_state_invalid");
        }
    }

    public static (string Verifier, string Challenge) CreatePkce()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var verifier = Base64Url(bytes);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
