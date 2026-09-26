using System.Text;
using MimeKit.Cryptography;

namespace MailClient.Application.Mail;

public static class MailAuthenticationParser
{
    private static readonly HashSet<string> Results = new(StringComparer.OrdinalIgnoreCase)
    {
        "pass", "fail", "softfail", "neutral", "none", "temperror", "permerror", "policy"
    };

    public static MailAuthenticationResponse? Parse(IEnumerable<MailHeaderResponse> headers)
    {
        var value = headers.FirstOrDefault(header =>
            string.Equals(header.Name, "Authentication-Results", StringComparison.OrdinalIgnoreCase))?.Value;
        if (string.IsNullOrWhiteSpace(value)
            || !AuthenticationResults.TryParse(Encoding.UTF8.GetBytes(value), out var parsed))
            return null;

        string? Result(string method)
        {
            var values = parsed.Results
                .Where(result => string.Equals(result.Method, method, StringComparison.OrdinalIgnoreCase))
                .Select(result => Normalize(result.Result))
                .Where(result => result is not null)
                .ToList();
            return values.Contains("pass", StringComparer.Ordinal) ? "pass" : values.FirstOrDefault();
        }

        var response = new MailAuthenticationResponse(
            parsed.AuthenticationServiceIdentifier,
            Result("spf"),
            Result("dkim"),
            Result("dmarc"));
        return response.Spf is null && response.Dkim is null && response.Dmarc is null ? null : response;
    }

    private static string? Normalize(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized == "hardfail")
            normalized = "fail";
        return Results.Contains(normalized) ? normalized : null;
    }
}
