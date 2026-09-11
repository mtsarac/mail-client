using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using MailClient.Application;
using MailClient.Application.Interfaces;
using MailClient.Application.Validation;
using Microsoft.AspNetCore.Http;

// Maps mail-account endpoints: CRUD, connection test, folders, refresh, and idempotent send.
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
            catch (RequestConflictException ex)
            {
                return Results.Conflict(ex.Errors.ToDictionary(entry => entry.Key, entry => entry.Value));
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
            catch (RequestConflictException ex)
            {
                return Results.Conflict(ex.Errors.ToDictionary(entry => entry.Key, entry => entry.Value));
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

        group.MapPost("/{id:guid}/send", async (
            Guid id,
            HttpRequest request,
            ClaimsPrincipal user,
            IMailSendService service,
            CancellationToken ct) =>
        {
            var form = await request.ReadFormAsync(ct);
            string? idempotencyKey = request.Headers.ContainsKey("Idempotency-Key")
                ? request.Headers["Idempotency-Key"].ToString().Trim()
                : null;
            if (string.IsNullOrWhiteSpace(idempotencyKey))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                { ["idempotencyKey"] = ["Idempotency-Key header is required."] });
            if (idempotencyKey.Length > 200)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                { ["idempotencyKey"] = ["Idempotency key must be 1-200 characters."] });
            var files = request.Form.Files
                .Select(file => new SendMailAttachment(
                    file.FileName, file.ContentType, file.OpenReadStream())).ToList();
            var result = await service.SendAsync(
                GetUserId(user),
                new SendMailCommand(
                    id,
                    form["toAddress"].ToString(),
                    form["subject"].ToString(),
                    ToNullIfEmpty(form["bodyHtml"].ToString()),
                    ToNullIfEmpty(form["bodyText"].ToString()),
                    files,
                    idempotencyKey),
                ct);
            return result.Outcome switch
            {
                ServiceOutcome.Ok when result.Value!.Sent => Results.Ok(result.Value),
                ServiceOutcome.Ok => Results.Problem(
                    title: "Mail send failed.",
                    detail: result.Value?.Warning,
                    statusCode: StatusCodes.Status502BadGateway),
                ServiceOutcome.NotFound => Results.NotFound(),
                ServiceOutcome.Conflict => Results.Conflict(
                    result.Errors.ToDictionary(entry => entry.Key, entry => entry.Value)),
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

    private static string? ToNullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    public sealed record MailFolderSyncRequest(bool IsSyncEnabled);
}
