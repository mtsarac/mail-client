namespace MailClient.Tests;

/// <summary>
/// Integration tests run only when explicitly enabled, so a plain `dotnet test` never
/// guesses at local service credentials. Set the variables below (see development docs).
/// </summary>
internal static class IntegrationEnvironment
{
    public static bool Skip => Value("MAILCLIENT_SKIP_INTEGRATION") == "1";
    public static string? PostgresAdmin => Value("MAILCLIENT_TEST_POSTGRES_ADMIN");
    public static bool PostgresEnabled => !Skip && PostgresAdmin is not null;
    public static bool GreenMailEnabled => !Skip && (Value("MAILCLIENT_TEST_GREENMAIL") == "1" || Value("MAILCLIENT_TEST_GREENMAIL_HOST") is not null);
    public static string GreenMailHost => Value("MAILCLIENT_TEST_GREENMAIL_HOST") ?? "127.0.0.1";
    public static int GreenMailImapPort => Port("MAILCLIENT_TEST_GREENMAIL_IMAP", 3143);
    public static string GreenMailUsername => Value("MAILCLIENT_TEST_GREENMAIL_USER") ?? "test@localhost";
    public static string GreenMailPassword => Value("MAILCLIENT_TEST_GREENMAIL_PASSWORD") ?? "test123";

    private static string? Value(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static int Port(string name, int fallback) => int.TryParse(Value(name), out var port) ? port : fallback;
}
