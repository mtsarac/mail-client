using MimeKit;

namespace MailClient.Infrastructure.Email;

/// <summary>
/// Recognizes the report mails produced by the receipts every send requests, so sync can keep them out of the mailbox.
/// Read receipts (MDN) are always hidden; delivery reports (DSN) only when every recipient was delivered, so bounces
/// and anything that cannot be parsed stay visible.
/// </summary>
public static class ReceiptReportDetector
{
    private static readonly string[] SuccessActions = ["delivered", "relayed", "expanded"];

    public static bool IsHiddenReceipt(MimeMessage message)
    {
        if (message.Body is not MultipartReport report)
            return false;
        if (string.Equals(report.ReportType, "disposition-notification", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.Equals(report.ReportType, "delivery-status", StringComparison.OrdinalIgnoreCase))
            return false;

        var status = report.OfType<MessageDeliveryStatus>().FirstOrDefault();
        if (status is null)
            return false;
        try
        {
            // The first group describes the message; each following group describes one recipient.
            var recipients = status.StatusGroups.Skip(1).ToList();
            return recipients.Count > 0 && recipients.All(IsSuccessful);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsSuccessful(HeaderList recipient)
    {
        var action = recipient["Action"]?.Trim();
        if (string.IsNullOrEmpty(action))
            return false;
        var end = action.IndexOfAny([' ', '\t', '(']);
        var value = end < 0 ? action : action[..end];
        return SuccessActions.Contains(value, StringComparer.OrdinalIgnoreCase);
    }
}
