using System.Text.RegularExpressions;

namespace MailClient.Application.Conversations;

public sealed record ConversationSummaryResponse(
    Guid Id,
    string Subject,
    IReadOnlyList<string> Participants,
    int MessageCount,
    int UnreadCount,
    bool HasAttachments,
    DateTime StartedAt,
    DateTime LastMessageAt);

public sealed record ConversationMessageResponse(
    Guid Id,
    Guid FolderId,
    string Subject,
    string FromAddress,
    string FromDisplayName,
    DateTime SentAt,
    DateTime ReceivedAt,
    bool IsRead,
    bool HasAttachments);

public sealed record ConversationDetailResponse(
    Guid Id,
    string Subject,
    IReadOnlyList<ConversationMessageResponse> Messages);

public static class ConversationEngine
{
    /// <summary>Removes Re:/Fwd:/FW:/SV:/TR style prefixes (repeated, case-insensitive, localized) and collapses whitespace.</summary>
    public static string NormalizeSubject(string? subject)
    {
        var value = subject?.Trim() ?? string.Empty;
        value = Regex.Replace(
            value,
            @"^(?:(?:RE|FWD|FW|SV|TR|ENC|ANTW|VV|VO)\s*:\s*)+",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        value = Regex.Replace(
            value,
            @"\s+",
            " ",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        return value.Trim();
    }

    /// <summary>Trims whitespace and strips single surrounding angle brackets. Idempotent.</summary>
    public static string NormalizeMessageId(string? messageId)
    {
        var value = messageId?.Trim() ?? string.Empty;
        while (value.Length >= 2 && value.StartsWith('<') && value.EndsWith('>'))
            value = value[1..^1].Trim();
        return value;
    }

    public static IReadOnlyList<string> ParseReferences(string? references) =>
        (references ?? string.Empty)
            .Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(reference => NormalizeMessageId(reference))
            .Where(reference => reference.Length > 0)
            .Distinct()
            .ToList();
}
