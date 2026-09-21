namespace MailClient.Api.OpenApi;

/// <summary>Documents the stable ProblemDetails <c>code</c> values an endpoint can return for one status.</summary>
public sealed record ProblemCodesMetadata(int Status, string[] Codes);

public static class ProblemCodesExtensions
{
    public static TBuilder ProblemCodes<TBuilder>(this TBuilder builder, int status, params string[] codes)
        where TBuilder : IEndpointConventionBuilder =>
        builder.ProducesProblem(status).WithMetadata(new ProblemCodesMetadata(status, codes));
}
