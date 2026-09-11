// Exception carrying validation errors, translated to 400 problem details at endpoints.
namespace MailClient.Application.Validation;

public sealed class RequestValidationException(IReadOnlyDictionary<string, string[]> errors)
    : Exception("The request is invalid.")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;

    public RequestValidationException(string field, string message)
        : this(new Dictionary<string, string[]> { [field] = [message] })
    {
    }
}
