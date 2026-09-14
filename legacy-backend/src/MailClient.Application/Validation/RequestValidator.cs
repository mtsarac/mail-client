using System.Net.Mail;
using System.Text.RegularExpressions;
using MailClient.Application.Auth;

// Shared request field validators accumulating per-field error dictionaries.
namespace MailClient.Application.Validation;

public static partial class RequestValidator
{
    public const int MaxEmailLength = 320;
    public const int MaxUsernameLength = 320;
    public const int MaxMailboxPasswordLength = 1024;
    public const int MaxIdempotencyKeyLength = 200;
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

        if (trimmed.Length > MaxEmailLength)
        {
            errors[field] = [$"Email address must be at most {MaxEmailLength} characters."];
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
        else if (value.Length > MaxMailboxPasswordLength)
            errors[field] = [$"Mailbox password must be at most {MaxMailboxPasswordLength} characters."];
    }

    public static void RequireUsername(string? value, string field, Dictionary<string, string[]> errors)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            errors[field] = ["Username is required."];
        else if (trimmed.Length > MaxUsernameLength)
            errors[field] = [$"Username must be at most {MaxUsernameLength} characters."];
    }

    public static void RequireIdempotencyKey(string? value, string field, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
            errors[field] = ["Idempotency key is required."];
        else if (value.Trim().Length > MaxIdempotencyKeyLength)
            errors[field] = [$"Idempotency key must be at most {MaxIdempotencyKeyLength} characters."];
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

    public static void RequireDefinedEnum<T>(T value, string field, Dictionary<string, string[]> errors)
        where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
            errors[field] = ["Value is invalid."];
    }

    public static void ThrowIfInvalid(Dictionary<string, string[]> errors)
    {
        if (errors.Count > 0)
            throw new RequestValidationException(errors);
    }
}
