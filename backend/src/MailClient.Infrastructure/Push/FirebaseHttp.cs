namespace MailClient.Infrastructure.Push;

public static class FirebaseHttp
{
    public static HttpClient Create() => new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    });
}
