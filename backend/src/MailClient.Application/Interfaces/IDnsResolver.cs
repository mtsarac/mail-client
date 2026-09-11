using System.Net;

// DNS resolution seam so host validation stays testable without the network.
namespace MailClient.Application.Interfaces;

public interface IDnsResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}
