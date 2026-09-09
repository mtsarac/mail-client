using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using MailClient.Application.Interfaces;
using MailClient.Application.Validation;

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

        group.MapPost("/", async (MailAccountRequest? request, ClaimsPrincipal user, IMailAccountService service, CancellationToken ct) =>
        {
            if (request is null)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = ["Request body is required."] });
            try
            {
                var account = await service.CreateAsync(GetUserId(user), request, ct);
                return Results.Created($"/api/mail-accounts/{account.Id}", account);
            }
            catch (RequestValidationException ex)
            {
                return Results.ValidationProblem(ex.Errors.ToDictionary(entry => entry.Key, entry => entry.Value));
            }
        });

        group.MapPut("/{id:guid}", async (Guid id, UpdateMailAccountRequest? request, ClaimsPrincipal user, IMailAccountService service, CancellationToken ct) =>
        {
            if (request is null)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = ["Request body is required."] });
            try
            {
                var account = await service.UpdateAsync(GetUserId(user), id, request, ct);
                return account is null ? Results.NotFound() : Results.Ok(account);
            }
            catch (RequestValidationException ex)
            {
                return Results.ValidationProblem(ex.Errors.ToDictionary(entry => entry.Key, entry => entry.Value));
            }
        });

        group.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, IMailAccountService service, CancellationToken ct) =>
            await service.DeleteAsync(GetUserId(user), id, ct) ? Results.NoContent() : Results.NotFound());

        group.MapPost("/{id:guid}/test", async (Guid id, ClaimsPrincipal user, IMailAccountService service, IMailFolderService folders, CancellationToken ct) =>
        {
            var result = await service.TestAsync(GetUserId(user), id, ct);
            if (result is null) return Results.NotFound();
            if (result.Succeeded)
                await folders.RefreshAsync(GetUserId(user), id, ct);
            return Results.Ok(result);
        }).RequireRateLimiting("mail-operations");

        group.MapGet("/{id:guid}/folders", async (Guid id, ClaimsPrincipal user, IMailFolderService service, CancellationToken ct) =>
        {
            var folders = await service.ListAsync(GetUserId(user), id, ct);
            return folders is null ? Results.NotFound() : Results.Ok(folders);
        });

        group.MapPost("/{id:guid}/folders/refresh", async (Guid id, ClaimsPrincipal user, IMailFolderService service, CancellationToken ct) =>
        {
            var result = await service.RefreshAsync(GetUserId(user), id, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).RequireRateLimiting("mail-operations");

        group.MapPatch("/{id:guid}/folders/{folderId:guid}/sync", async (Guid id, Guid folderId, MailFolderSyncRequest? request, ClaimsPrincipal user, IMailFolderService service, CancellationToken ct) =>
        {
            if (request is null)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = ["Request body is required."] });
            var folder = await service.SetSyncEnabledAsync(GetUserId(user), id, folderId, request.IsSyncEnabled, ct);
            return folder is null ? Results.NotFound() : Results.Ok(folder);
        });

        return app;
    }

    private static Guid GetUserId(ClaimsPrincipal user)
    {
        var value = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue(JwtRegisteredClaimNames.Sub);
        return Guid.TryParse(value, out var userId) ? userId : throw new UnauthorizedAccessException();
    }

    public sealed record MailFolderSyncRequest(bool IsSyncEnabled);
}
