using System.Net;
using System.Net.Sockets;
using MailClient.Application.Interfaces;

namespace MailClient.Infrastructure.Network;

public sealed class OutboundHostValidator(IDnsResolver dns) : IOutboundHostValidator
{
    private static readonly string[] LocalhostNames = ["localhost"];

    public HostCheckResult CheckLiteralHost(string? host)
    {
        var trimmed = host?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return HostCheckResult.Deny("Host is missing.");

        if (LocalhostNames.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            return HostCheckResult.Deny("Host resolves to a loopback name.");

        if (IPAddress.TryParse(trimmed, out var address))
            return CheckAddress(address);

        return HostCheckResult.Allow();
    }

    public async Task<HostCheckResult> CheckAsync(string? host, CancellationToken cancellationToken)
    {
        var literal = CheckLiteralHost(host);
        if (!literal.Allowed)
            return literal;

        var trimmed = host!.Trim();
        if (IPAddress.TryParse(trimmed, out _))
            return literal;

        IPAddress[] addresses;
        try
        {
            addresses = await dns.ResolveAsync(trimmed, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HostCheckResult.Deny($"Host could not be resolved ({ex.GetType().Name}).");
        }

        if (addresses.Length == 0)
            return HostCheckResult.Deny("Host resolved to no addresses.");

        foreach (var address in addresses)
        {
            var result = CheckAddress(address);
            if (!result.Allowed)
                return HostCheckResult.Deny($"Host resolves to a blocked address ({result.Reason}).");
        }

        return HostCheckResult.Allow();
    }

    internal static HostCheckResult CheckAddress(IPAddress address)
    {
        var candidate = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        if (IPAddress.IsLoopback(candidate))
            return HostCheckResult.Deny("Loopback address.");

        if (candidate.AddressFamily == AddressFamily.InterNetworkV6)
            return CheckIPv6(candidate);

        return CheckIPv4(candidate);
    }

    private static HostCheckResult CheckIPv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        var first = bytes[0];
        var second = bytes[1];

        if (first == 0 || first == 127)
            return HostCheckResult.Deny("Loopback or current-network address.");
        if (first == 10)
            return HostCheckResult.Deny("Private IPv4 range (10.0.0.0/8).");
        if (first == 172 && second is >= 16 and <= 31)
            return HostCheckResult.Deny("Private IPv4 range (172.16.0.0/12).");
        if (first == 192 && second == 168)
            return HostCheckResult.Deny("Private IPv4 range (192.168.0.0/16).");
        if (first == 169 && second == 254)
            return HostCheckResult.Deny("Link-local IPv4 range (169.254.0.0/16).");
        if (first == 100 && second is >= 64 and <= 127)
            return HostCheckResult.Deny("Shared address space (100.64.0.0/10).");
        if (first == 192 && second == 0 && (bytes[2] == 0 || bytes[2] == 2))
            return HostCheckResult.Deny("Special-use IPv4 range.");
        if (first == 198 && (second == 18 || second == 19))
            return HostCheckResult.Deny("Benchmarking range (198.18.0.0/15).");
        if (first == 198 && second == 51 && bytes[2] == 100)
            return HostCheckResult.Deny("Documentation range (198.51.100.0/24).");
        if (first == 203 && second == 0 && bytes[2] == 113)
            return HostCheckResult.Deny("Documentation range (203.0.113.0/24).");
        if (first == 192 && second == 88 && bytes[2] == 99)
            return HostCheckResult.Deny("Deprecated relay range (192.88.99.0/24).");
        if (first is >= 224 and <= 239)
            return HostCheckResult.Deny("Multicast address.");
        if (first >= 240 || bytes.All(b => b == 255))
            return HostCheckResult.Deny("Reserved or broadcast address.");

        return HostCheckResult.Allow();
    }

    private static HostCheckResult CheckIPv6(IPAddress address)
    {
        if (address.Equals(IPAddress.IPv6None))
            return HostCheckResult.Deny("Unspecified address.");

        var bytes = address.GetAddressBytes();
        var first = bytes[0];
        var second = bytes[1];

        if ((first & 0xFF) == 0xFF)
            return HostCheckResult.Deny("Multicast address.");
        if (first == 0xFE && (second & 0xC0) == 0x80)
            return HostCheckResult.Deny("Link-local IPv6 range (fe80::/10).");
        if ((first & 0xFE) == 0xFC)
            return HostCheckResult.Deny("Unique-local IPv6 range (fc00::/7).");
        if (first == 0x20 && second == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8)
            return HostCheckResult.Deny("Documentation range (2001:db8::/32).");

        return HostCheckResult.Allow();
    }
}
