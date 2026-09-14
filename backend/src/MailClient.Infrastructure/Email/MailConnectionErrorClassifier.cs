using System.Net.Sockets;
using MailClient.Application.Mail;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace MailClient.Infrastructure.Email;

public static class MailConnectionErrorClassifier
{
    public static MailConnectionFailure Classify(Exception exception) => exception switch
    {
        AuthenticationException => MailConnectionFailure.Authentication,
        SslHandshakeException => MailConnectionFailure.Tls,
        System.Security.Authentication.AuthenticationException => MailConnectionFailure.Tls,
        SocketException => MailConnectionFailure.Network,
        IOException => MailConnectionFailure.Network,
        TimeoutException => MailConnectionFailure.Network,
        ImapProtocolException => MailConnectionFailure.Protocol,
        ImapCommandException => MailConnectionFailure.Protocol,
        SmtpProtocolException => MailConnectionFailure.Protocol,
        SmtpCommandException => MailConnectionFailure.Protocol,
        ServiceNotConnectedException => MailConnectionFailure.Protocol,
        ServiceNotAuthenticatedException => MailConnectionFailure.Authentication,
        ProtocolException => MailConnectionFailure.Protocol,
        _ => MailConnectionFailure.Network
    };

    public static string SafeMessage(MailConnectionFailure failure) => failure switch
    {
        MailConnectionFailure.Authentication => "Mail authentication failed.",
        MailConnectionFailure.Tls => "Mail TLS handshake failed.",
        MailConnectionFailure.Protocol => "Mail protocol error.",
        _ => "Could not reach the mail server."
    };
}
