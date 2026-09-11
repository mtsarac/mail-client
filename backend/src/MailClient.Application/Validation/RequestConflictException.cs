// Exception carrying conflict errors, translated to 409 responses at endpoints.
namespace MailClient.Application.Validation;

// Thrown when a request conflicts with an existing row protected by a
// database unique constraint (e.g. duplicate mail account). Endpoints map
// this to HTTP 409 Conflict. The database constraint remains the final
// authority; this exception is raised both by application-level pre-checks
// and by race-safe unique-violation handling.
public sealed class RequestConflictException : Exception
{
    public IReadOnlyDictionary<string, string[]> Errors { get; }

    public RequestConflictException(IReadOnlyDictionary<string, string[]> errors)
        : base("The request conflicts with an existing resource.")
    {
        Errors = errors;
    }

    public RequestConflictException(string field, string message)
        : this(new Dictionary<string, string[]> { [field] = [message] })
    {
    }
}
