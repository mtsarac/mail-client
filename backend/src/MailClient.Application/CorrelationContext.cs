// Ambient correlation id carried across async flow without HTTP dependencies.
// The API middleware sets this per request; the audit logger reads it.
namespace MailClient.Application;

public static class CorrelationContext
{
    private static readonly AsyncLocal<string?> CurrentValue = new();

    public static string? Current
    {
        get => CurrentValue.Value;
        set => CurrentValue.Value = value;
    }
}
