using System.Net;
using System.Net.Sockets;

namespace MailClient.Infrastructure.Network;

public interface IDnsResolver { Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken); }
public sealed record HostValidationResult(bool Allowed, string? Reason = null);

public sealed record ValidatedHost(string Host, IPAddress Address);

public class OutboundHostValidator(IDnsResolver dns, bool allowPrivateHosts = false)
{
    public virtual async Task<HostValidationResult> ValidateAsync(string host, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host) || (!allowPrivateHosts && host.Equals("localhost", StringComparison.OrdinalIgnoreCase))) return new(false, "Invalid or local host.");
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal)) addresses = [literal];
        else
        {
            try { addresses = await dns.ResolveAsync(host, cancellationToken); }
            catch (Exception exception) when (exception is not OperationCanceledException) { return new(false, "DNS resolution failed."); }
        }
        return addresses.Length > 0 && addresses.All(address => allowPrivateHosts || IsPublic(address)) ? new(true) : new(false, "Host resolves to blocked address.");
    }

    public virtual async Task<ValidatedHost> ResolveAllowedAsync(string host, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host) || (!allowPrivateHosts && host.Equals("localhost", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Invalid or local host.");
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
                throw new InvalidOperationException("DNS resolution failed.", exception);
            }
        }

        if (addresses.Length == 0)
            throw new InvalidOperationException("Host resolved to no addresses.");
        if (!allowPrivateHosts)
            foreach (var address in addresses)
                if (!IsPublic(address))
                    throw new InvalidOperationException("Host resolves to blocked address.");
        var selected = addresses[0];
        return new ValidatedHost(host, selected.IsIPv4MappedToIPv6 ? selected.MapToIPv4() : selected);
    }

    private static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6) return bytes[0] != 0xFF && (bytes[0] & 0xFE) != 0xFC && !(bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80);
        return bytes[0] != 0 && bytes[0] != 10 && bytes[0] != 127 && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) && !(bytes[0] == 192 && bytes[1] == 168) && !(bytes[0] == 169 && bytes[1] == 254) && bytes[0] < 224;
    }
}
