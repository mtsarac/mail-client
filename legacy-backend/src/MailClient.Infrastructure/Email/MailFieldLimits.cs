// Field-length caps and truncation helpers for incoming mail data.
namespace MailClient.Infrastructure.Email;

// Keep in sync with EF Core configurations:
// MailConfiguration (Mail) and AttachmentConfiguration (Attachment).
internal static class MailFieldLimits
{
    public const int Subject = 500;
    public const int FromAddress = 320;
    public const int FromDisplayName = 250;
    public const int ToAddress = 320;
    public const int MessageId = 998;

    public const int FileName = 255;
    public const int ContentType = 150;
    public const int ContentId = 998;
}

internal static class MailFieldNormalizer
{
    public static string Subject(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "(no subject)" : Truncate(value.Trim(), MailFieldLimits.Subject);

    public static string Address(string? value) =>
        Truncate(value?.Trim() ?? string.Empty, MailFieldLimits.FromAddress);

    public static string ToAddress(string? value) =>
        Truncate(value?.Trim() ?? string.Empty, MailFieldLimits.ToAddress);

    public static string DisplayName(string? value) =>
        Truncate(value?.Trim() ?? string.Empty, MailFieldLimits.FromDisplayName);

    public static string MessageId(string? value) =>
        Truncate(value?.Trim() ?? string.Empty, MailFieldLimits.MessageId);

    public static string FileName(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "attachment" : Truncate(value.Trim(), MailFieldLimits.FileName);

    public static string ContentType(string? value) =>
        Truncate(string.IsNullOrWhiteSpace(value) ? "application/octet-stream" : value.Trim(), MailFieldLimits.ContentType);

    public static string ContentId(string? value) =>
        Truncate(value?.Trim() ?? string.Empty, MailFieldLimits.ContentId);

    public static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
