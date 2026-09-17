using MailClient.Domain.Enums;

namespace MailClient.Application.Sync;

public static class SyncFailureClassifier
{
    public static SyncFailureCategory Classify(Exception exception) => exception switch
    {
        InvalidOperationException invalid when IsAuthenticationMessage(invalid.Message) =>
            SyncFailureCategory.Authentication,
        InvalidOperationException invalid when IsConfigurationMessage(invalid.Message) =>
            SyncFailureCategory.Configuration,
        TimeoutException => SyncFailureCategory.Transient,
        IOException => SyncFailureCategory.Transient,
        System.Net.Sockets.SocketException => SyncFailureCategory.Transient,
        _ => SyncFailureCategory.Permanent
    };

    public static bool IsAuthentication(Exception exception) =>
        Classify(exception) == SyncFailureCategory.Authentication;

    private static bool IsAuthenticationMessage(string message) =>
        message is "credential_missing"
            or "mail_account_not_found"
            or "mail_account_disabled"
            or "mail_account_needs_reauthentication"
            or "mail_authentication_failed";

    private static bool IsConfigurationMessage(string message) =>
        message is "oauth_provider_not_configured"
            or "oauth_code_exchange_failed";
}
