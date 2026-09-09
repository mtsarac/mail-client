using System.Net.Mail;
using System.Text.RegularExpressions;
using MailClient.Application.Auth;

namespace MailClient.Application.Validation;

public static partial class RequestValidator
{
    private const int MaxHostLength = 253;
    private const int MaxLabelLength = 63;

    [GeneratedRegex(@"^(?=.{1,253}$)([A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)*[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?$")]
    private static partial Regex HostnameRegex();

    public static void RequireEmail(string? value, string field, Dictionary<string, string[]> errors)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            errors[field] = ["Email address is required."];
            return;
        }

        try
        {
            PasswordPolicy.ValidateEmail(trimmed);
        }
        catch (ArgumentException)
        {
            errors[field] = ["Email address format is invalid."];
        }
    }

    public static void RequirePassword(string? value, string field, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors[field] = ["Password is required."];
            return;
        }

        try
        {
            PasswordPolicy.ValidatePassword(value);
        }
        catch (ArgumentException ex)
        {
            errors[field] = [ex.Message];
        }
    }

    public static void RequireMailboxPassword(string? value, string field, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
            errors[field] = ["Mailbox password is required."];
    }

    public static void RequireDisplayName(string? value, string field, int maxLength, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
            errors[field] = ["Display name is required."];
        else if (value.Trim().Length > maxLength)
            errors[field] = [$"Display name must be at most {maxLength} characters."];
    }

    public static void RequireHost(string? value, string field, Dictionary<string, string[]> errors)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            errors[field] = ["Host is required."];
            return;
        }

        if (trimmed.Length > MaxHostLength || trimmed.Any(char.IsWhiteSpace))
        {
            errors[field] = ["Host format is invalid."];
            return;
        }

        if (System.Net.IPAddress.TryParse(trimmed, out _))
            return;

        var labels = trimmed.Split('.');
        if (labels.Any(label => label.Length == 0 || label.Length > MaxLabelLength) || !HostnameRegex().IsMatch(trimmed))
            errors[field] = ["Host format is invalid."];
    }

    public static void RequirePort(int value, string field, Dictionary<string, string[]> errors)
    {
        if (value is < 1 or > 65535)
            errors[field] = ["Port must be between 1 and 65535."];
    }

    public static void ThrowIfInvalid(Dictionary<string, string[]> errors)
    {
        if (errors.Count > 0)
            throw new RequestValidationException(errors);
    }
}
