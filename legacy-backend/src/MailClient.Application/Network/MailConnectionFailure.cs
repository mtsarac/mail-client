// Classified mail-connection failure kinds plus the connection exception type.
namespace MailClient.Application.Network;

public enum MailConnectionFailure
{
    Authentication,
    Network,
    Tls,
    Protocol
}

public sealed class MailConnectionException(MailConnectionFailure failure, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public MailConnectionFailure Failure { get; } = failure;
}
