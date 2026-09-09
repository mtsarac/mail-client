using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using MailClient.Application.Interfaces;

namespace MailClient.Api.Accounts;

public static class MailAccountEndpoints
{
    public static IEndpointRouteBuilder MapMailAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/mail-accounts").RequireAuthorization().WithTags("Mail Accounts");

        group.MapGet("/", async (ClaimsPrincipal user, IMailAccountService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(GetUserId(user), ct)));

        group.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, IMailAccountService service, CancellationToken ct) =>
        {
            var account = await service.GetAsync(GetUserId(user), id, ct);
            return account is null ? Results.NotFound() : Results.Ok(account);
        });

        group.MapPost("/", async (MailAccountRequest request, ClaimsPrincipal user, IMailAccountService service, CancellationToken ct) =>
        {
            try
            {
                var account = await service.CreateAsync(GetUserId(user), request, ct);
                return Results.Created($"/api/mail-accounts/{account.Id}", account);
            }
            catch (ArgumentException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["mailAccount"] = [ex.Message] });
            }
        });

        group.MapPut("/{id:guid}", async (Guid id, MailAccountRequest request, ClaimsPrincipal user, IMailAccountService service, CancellationToken ct) =>
        {
            try
            {
                var account = await service.UpdateAsync(GetUserId(user), id, request, ct);
                return account is null ? Results.NotFound() : Results.Ok(account);
            }
            catch (ArgumentException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["mailAccount"] = [ex.Message] });
            }
        });

        group.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, IMailAccountService service, CancellationToken ct) =>
            await service.DeleteAsync(GetUserId(user), id, ct) ? Results.NoContent() : Results.NotFound());

        group.MapPost("/{id:guid}/test", async (Guid id, ClaimsPrincipal user, IMailAccountService service, CancellationToken ct) =>
        {
            var result = await service.TestAsync(GetUserId(user), id, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        return app;
    }

    private static Guid GetUserId(ClaimsPrincipal user)
    {
        var value = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue(JwtRegisteredClaimNames.Sub);
        return Guid.TryParse(value, out var userId) ? userId : throw new UnauthorizedAccessException();
    }
}
