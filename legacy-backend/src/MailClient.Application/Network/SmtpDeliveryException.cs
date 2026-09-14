// SMTP delivery failure carrying the underlying transport error.
namespace MailClient.Application.Network;

// Thrown when an SMTP send was attempted but the application cannot prove
// whether the server accepted the message (interruption after DATA, lost
// response, ambiguous transport failure). Never carries secrets.
public sealed class SmtpDeliveryException(string message, Exception? inner = null)
    : Exception(message, inner)
{
}
