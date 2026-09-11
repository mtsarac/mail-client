// Health-probe seam backing the /health/db readiness endpoint.
namespace MailClient.Application.Interfaces;

public interface IHealthProbe
{
    Task<bool> CheckDatabaseAsync(CancellationToken cancellationToken);
}
