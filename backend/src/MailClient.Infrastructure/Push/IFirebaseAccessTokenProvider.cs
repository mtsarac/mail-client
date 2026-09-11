namespace MailClient.Infrastructure.Push;

public interface IFirebaseAccessTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken);
}
