using System.Net;
using System.Net.Sockets;

namespace MailClient.Infrastructure.Network;

public interface IDnsResolver { Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken); }
public sealed record HostValidationResult(bool Allowed, string? Reason = null);

public sealed record ValidatedHost(string Host, IPAddress Address);

public class OutboundHostValidator(IDnsResolver dns, bool allowPrivateHosts = false)
{
    // Non-globally-reachable ranges from the IANA IPv4/IPv6 special-purpose registries, plus multicast and
    // IPv6 transition prefixes (NAT64, 6to4, Teredo, IPv4-compatible) that can embed a blocked IPv4 target.
    private static readonly IPNetwork[] BlockedNetworks =
    [
        IPNetwork.Parse("0.0.0.0/8"),
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.0.0.0/24"),
        IPNetwork.Parse("192.0.2.0/24"),
        IPNetwork.Parse("192.88.99.0/24"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("198.18.0.0/15"),
        IPNetwork.Parse("198.51.100.0/24"),
        IPNetwork.Parse("203.0.113.0/24"),
        IPNetwork.Parse("224.0.0.0/4"),
        IPNetwork.Parse("240.0.0.0/4"),
        IPNetwork.Parse("::/96"),
        IPNetwork.Parse("::1/128"),
        IPNetwork.Parse("64:ff9b::/96"),
        IPNetwork.Parse("64:ff9b:1::/48"),
        IPNetwork.Parse("100::/64"),
        IPNetwork.Parse("2001::/23"),
        IPNetwork.Parse("2001:db8::/32"),
        IPNetwork.Parse("2002::/16"),
        IPNetwork.Parse("fc00::/7"),
        IPNetwork.Parse("fe80::/10"),
        IPNetwork.Parse("fec0::/10"),
        IPNetwork.Parse("ff00::/8")
    ];

    public virtual async Task<HostValidationResult> ValidateAsync(string host, CancellationToken cancellationToken)
    {
        var (_, reason, _) = await ResolveCoreAsync(host, cancellationToken);
        return new(reason is null, reason);
    }

    public virtual async Task<ValidatedHost> ResolveAllowedAsync(string host, CancellationToken cancellationToken)
    {
        var (address, reason, cause) = await ResolveCoreAsync(host, cancellationToken);
        return reason is null ? new ValidatedHost(host, address!) : throw new InvalidOperationException(reason, cause);
    }

    /// <summary>Resolves the host once and returns the address to connect to, or why the host is not allowed.</summary>
    private async Task<(IPAddress? Address, string? Reason, Exception? Cause)> ResolveCoreAsync(string host, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host) || (!allowPrivateHosts && host.Equals("localhost", StringComparison.OrdinalIgnoreCase)))
            return (null, "Invalid or local host.", null);
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
            addresses = [literal];
        else
        {
            try
            {
                addresses = await dns.ResolveAsync(host, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return (null, "DNS resolution failed.", exception);
            }
        }

        if (addresses.Length == 0)
            return (null, "Host resolved to no addresses.", null);
        // Every resolved address must be public, not just the one we connect to, so DNS answers cannot mix in a
        // private target.
        if (!allowPrivateHosts && !addresses.All(IsPublic))
            return (null, "Host resolves to blocked address.", null);
        var selected = addresses[0];
        return (selected.IsIPv4MappedToIPv6 ? selected.MapToIPv4() : selected, null, null);
    }

    private static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        return address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6
            && !BlockedNetworks.Any(network => network.Contains(address));
    }
}
