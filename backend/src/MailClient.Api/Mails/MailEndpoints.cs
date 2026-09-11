using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using MailClient.Application;
using MailClient.Application.Interfaces;
using MailClient.Domain.Enums;

// Maps mail reading endpoints: unified list, detail, attachment download, and mark read.
namespace MailClient.Api.Mails;

public static class MailEndpoints
{
    public static IEndpointRouteBuilder MapMailEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/mails").RequireAuthorization().WithTags("Mails");

        group.MapGet("/", async (
            string? folderType,
            Guid? accountId,
            Guid? folderId,
            ClaimsPrincipal user,
            IMailQueryService service,
            CancellationToken ct,
            int page = 1,
            int pageSize = 30) =>
        {
            MailFolderType? type = null;
            if (folderType is not null)
            {
                if (!Enum.TryParse(folderType, ignoreCase: true, out MailFolderType parsed)
                    || !Enum.IsDefined(parsed))
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["folderType"] = ["Unknown folder type."] });
                type = parsed;
            }

            var result = await service.ListAsync(
                GetUserId(user), new MailListQuery(accountId, folderId, type, page, pageSize), ct);
            return result.Outcome switch
            {
                ServiceOutcome.Ok => Results.Ok(result.Value),
                ServiceOutcome.NotFound => Results.NotFound(),
                _ => Results.ValidationProblem(result.Errors.ToDictionary(entry => entry.Key, entry => entry.Value))
            };
        });

        group.MapGet("/{id:guid}", async (
            Guid id,
            ClaimsPrincipal user,
            IMailQueryService service,
            CancellationToken ct) =>
        {
            var result = await service.GetDetailAsync(GetUserId(user), id, ct);
            return result.Outcome == ServiceOutcome.Ok ? Results.Ok(result.Value) : Results.NotFound();
        });

        group.MapGet("/{mailId:guid}/attachments/{attachmentId:guid}", async (
            Guid mailId,
            Guid attachmentId,
            ClaimsPrincipal user,
            IMailQueryService service,
            CancellationToken ct) =>
        {
            var result = await service.GetAttachmentAsync(GetUserId(user), mailId, attachmentId, ct);
            return result.Outcome == ServiceOutcome.Ok && result.Value is not null
                ? Results.File(result.Value.Content, result.Value.ContentType, result.Value.FileName)
                : Results.NotFound();
        });

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
