using System.Text;
using MailClient.Infrastructure.Email;
using MimeKit;

namespace MailClient.Tests;

public sealed class ReceiptReportDetectorTests
{
    [Fact]
    public void ReadReceipt_IsHidden() =>
        Assert.True(ReceiptReportDetector.IsHiddenReceipt(ReceiptReports.Mdn()));

    [Fact]
    public void DeliveredOnlyDsn_IsHidden() =>
        Assert.True(ReceiptReportDetector.IsHiddenReceipt(ReceiptReports.Dsn("delivered", "relayed")));

    [Theory]
    [InlineData("failed")]
    [InlineData("delayed")]
    public void DsnWithAnyUnsuccessfulRecipient_StaysVisible(string action) =>
        Assert.False(ReceiptReportDetector.IsHiddenReceipt(ReceiptReports.Dsn("delivered", action)));

    [Fact]
    public void DsnWithoutRecipientStatus_StaysVisible() =>
        Assert.False(ReceiptReportDetector.IsHiddenReceipt(ReceiptReports.Dsn()));

    [Fact]
    public void PlainMail_StaysVisible()
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("friend@example.test"));
        message.Subject = "Delivered: our plans";
        message.Body = new TextPart("plain") { Text = "Action: delivered" };

        Assert.False(ReceiptReportDetector.IsHiddenReceipt(message));
    }
}

/// <summary>Raw report mails as providers send them, parsed through MimeKit like sync does.</summary>
internal static class ReceiptReports
{
    public static MimeMessage Dsn(params string[] actions)
    {
        var recipients = string.Concat(actions.Select((action, index) =>
            $"\r\nFinal-Recipient: rfc822; friend{index}@example.test\r\nAction: {action}\r\nStatus: {(action == "failed" ? "5.1.1" : "2.0.0")}\r\n"));
        return Parse($"""
            From: Mail Delivery System <mailer-daemon@example.test>
            To: me@example.test
            Subject: Delivery Status Notification
            Message-ID: <dsn-{Guid.NewGuid():N}@example.test>
            MIME-Version: 1.0
            Content-Type: multipart/report; report-type=delivery-status; boundary="b"

            --b
            Content-Type: text/plain

            Your message was processed.
            --b
            Content-Type: message/delivery-status

            Reporting-MTA: dns; mx.example.test
            {recipients}
            --b--
            """);
    }

    public static MimeMessage Mdn() => Parse($"""
        From: friend@example.test
        To: me@example.test
        Subject: Read: Hello
        Message-ID: <mdn-{Guid.NewGuid():N}@example.test>
        MIME-Version: 1.0
        Content-Type: multipart/report; report-type=disposition-notification; boundary="b"

        --b
        Content-Type: text/plain

        Your message was displayed.
        --b
        Content-Type: message/disposition-notification

        Final-Recipient: rfc822; friend@example.test
        Disposition: manual-action/MDN-sent-manually; displayed
        --b--
        """);

    private static MimeMessage Parse(string raw) =>
        MimeMessage.Load(new MemoryStream(Encoding.ASCII.GetBytes(raw.ReplaceLineEndings("\r\n"))));
}
