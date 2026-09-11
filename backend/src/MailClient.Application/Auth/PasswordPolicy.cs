using System.Net.Mail;

namespace MailClient.Application.Auth;

public static class PasswordPolicy
{
    public const int MinimumLength = 8;
    public const int MaximumLength = 128;

    public static void ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < MinimumLength)
            throw new ArgumentException($"Password must be at least {MinimumLength} characters long.");
        if (password.Length > MaximumLength)
            throw new ArgumentException($"Password must be at most {MaximumLength} characters long.");
    }

    public static void ValidateEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("Email address format is invalid.");
        var trimmed = email.Trim();
        try
        {
            var address = new MailAddress(trimmed);
            if (!string.Equals(address.Address, trimmed, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Email address format is invalid.");
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("Email address format is invalid.", ex);
        }
    }
}
