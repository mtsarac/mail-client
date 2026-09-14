using System.Net;
using MailClient.Application.Interfaces;

// System DNS resolver backing outbound host validation.
namespace MailClient.Infrastructure.Network;

public sealed class SystemDnsResolver : IDnsResolver
{
    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        return await Dns.GetHostAddressesAsync(host.Trim(), cancellationToken);
    }
}
