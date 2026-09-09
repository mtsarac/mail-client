namespace MailClient.Application.Interfaces;

public sealed record HostCheckResult(bool Allowed, string Reason)
{
    public static HostCheckResult Allow() => new(true, "Host is allowed.");
    public static HostCheckResult Deny(string reason) => new(false, reason);
}

public interface IOutboundHostValidator
{
    HostCheckResult CheckLiteralHost(string? host);

    Task<HostCheckResult> CheckAsync(string? host, CancellationToken cancellationToken);
}
