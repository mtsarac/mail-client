using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using MailClient.Application;
using MailClient.Application.Interfaces;

namespace MailClient.Api.Mails;

public static class MailEndpoints
{
    public static IEndpointRouteBuilder MapMailEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/mails").RequireAuthorization().WithTags("Mails");

        group.MapPatch("/{id:guid}/read", async (
            Guid id,
            SetReadRequest? request,
            ClaimsPrincipal user,
            IMailReadService service,
            CancellationToken ct) =>
        {
            if (request is null)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = ["Request body is required."] });
            var result = await service.SetReadAsync(GetUserId(user), id, request.IsRead, ct);
            return result.Outcome switch
            {
                ServiceOutcome.Ok => Results.Ok(result.Value),
                ServiceOutcome.NotFound => Results.NotFound(),
                ServiceOutcome.Conflict => Results.Conflict(result.Errors.ToDictionary(entry => entry.Key, entry => entry.Value)),
                ServiceOutcome.ProviderError => Results.Problem(
                    title: "Mail server operation failed.",
                    statusCode: StatusCodes.Status502BadGateway),
                _ => Results.ValidationProblem(result.Errors.ToDictionary(entry => entry.Key, entry => entry.Value))
            };
        }).RequireRateLimiting("mail-operations");

        return app;
    }

    private static Guid GetUserId(ClaimsPrincipal user)
    {
        var value = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue(JwtRegisteredClaimNames.Sub);
        return Guid.TryParse(value, out var userId) ? userId : throw new UnauthorizedAccessException();
    }

    public sealed record SetReadRequest(bool IsRead);
}
