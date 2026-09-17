using MailClient.Domain.Enums;

namespace MailClient.Application.Accounts;

public sealed record DiscoverRequest(string Email);
public sealed record DiscoverResponse(string DiscoveryId, string Email, MailProvider Provider, IReadOnlyList<AuthenticationMethod> AuthenticationMethods, bool ManualSetupAvailable);
public sealed record AuthenticationInput(AuthenticationMethod Type, string Password);
public sealed record EndpointInput(string Host, int Port, MailSecurity Security);
public sealed record ConnectRequest(string DiscoveryId, AuthenticationInput Authentication, string? DeviceIdentifier = null);
public sealed record AccountReconnectRequest(AuthenticationInput Authentication, EndpointInput? Imap = null, EndpointInput? Smtp = null);
public sealed record ManualConnectRequest(string Email, string Username, AuthenticationInput Authentication, EndpointInput Imap, EndpointInput Smtp, string? DisplayName = null, string? DeviceIdentifier = null);
public sealed record TokenResponse(string AccessToken, string RefreshToken, Guid MailAccountId, DateTime AccessTokenExpiresAt);
public sealed record RefreshRequest(string RefreshToken);
public sealed record LogoutRequest(string RefreshToken);
public sealed record AccountResponse(Guid Id, string EmailAddress, string DisplayName, MailProvider Provider, MailAccountStatus Status);
public sealed record OAuthStartRequest(string Email, string? DeviceIdentifier = null);
public sealed record OAuthStartResponse(string AuthorizationUrl, string State);
public sealed record OAuthCompleteRequest(string State, string Code);

public interface ICredentialProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedValue);
}

public interface IJwtTokenIssuer
{
    (string Token, DateTime ExpiresAt) Issue(Guid mailAccountId);
}

public interface ICurrentMailAccount
{
    Guid MailAccountId { get; }
}
