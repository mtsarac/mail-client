using System.Net.Sockets;
using System.Runtime.CompilerServices;
using MailClient.Application.Network;
using MailClient.Infrastructure.Email;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace MailClient.Infrastructure.Tests.Email;

public class MailConnectionErrorClassifierTests
{
    [Theory]
    [InlineData(typeof(AuthenticationException), MailConnectionFailure.Authentication)]
    [InlineData(typeof(SslHandshakeException), MailConnectionFailure.Tls)]
    [InlineData(typeof(System.Security.Authentication.AuthenticationException), MailConnectionFailure.Tls)]
    [InlineData(typeof(SocketException), MailConnectionFailure.Network)]
    [InlineData(typeof(IOException), MailConnectionFailure.Network)]
    [InlineData(typeof(TimeoutException), MailConnectionFailure.Network)]
    [InlineData(typeof(ImapProtocolException), MailConnectionFailure.Protocol)]
    [InlineData(typeof(ImapCommandException), MailConnectionFailure.Protocol)]
    [InlineData(typeof(SmtpProtocolException), MailConnectionFailure.Protocol)]
    [InlineData(typeof(SmtpCommandException), MailConnectionFailure.Protocol)]
    [InlineData(typeof(ServiceNotConnectedException), MailConnectionFailure.Protocol)]
    [InlineData(typeof(ServiceNotAuthenticatedException), MailConnectionFailure.Authentication)]
    public void Classify_MapsExceptionToFailureCategory(Type exceptionType, MailConnectionFailure expected)
    {
        var exception = (Exception)RuntimeHelpers.GetUninitializedObject(exceptionType);

        Assert.Equal(expected, MailConnectionErrorClassifier.Classify(exception));
    }

    [Fact]
    public void SafeMessage_DoesNotLeakDetails()
    {
        foreach (var failure in Enum.GetValues<MailConnectionFailure>())
        {
            var message = MailConnectionErrorClassifier.SafeMessage(failure);
            Assert.False(string.IsNullOrWhiteSpace(message));
            Assert.DoesNotContain("password", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("127.0.0.1", message, StringComparison.OrdinalIgnoreCase);
        }
    }
}
