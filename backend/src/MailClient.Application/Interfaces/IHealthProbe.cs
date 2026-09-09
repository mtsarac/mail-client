namespace MailClient.Application.Interfaces;

public interface IHealthProbe
{
    Task<bool> CheckDatabaseAsync(CancellationToken cancellationToken);
}
