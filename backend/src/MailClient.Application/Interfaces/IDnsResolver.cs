using System.Net;

namespace MailClient.Application.Interfaces;

public interface IDnsResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}
