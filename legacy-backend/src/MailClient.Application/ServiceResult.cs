// Service-layer result envelope (outcome + value + field errors) shared by all use cases.
namespace MailClient.Application;

public enum ServiceOutcome
{
    Ok,
    NotFound,
    Conflict,
    Invalid,
    Unauthorized,
    Forbidden,
    ProviderError
}

public sealed record ServiceResult<T>(ServiceOutcome Outcome, T? Value, IReadOnlyDictionary<string, string[]> Errors)
{
    public bool Succeeded => Outcome == ServiceOutcome.Ok;

    public static ServiceResult<T> Success(T value) =>
        new(ServiceOutcome.Ok, value, new Dictionary<string, string[]>());

    public static ServiceResult<T> Failure(ServiceOutcome outcome, IReadOnlyDictionary<string, string[]> errors) =>
        new(outcome, default, errors);

    public static ServiceResult<T> Failure(ServiceOutcome outcome, string field, string message) =>
        new(outcome, default, new Dictionary<string, string[]> { [field] = [message] });
}
