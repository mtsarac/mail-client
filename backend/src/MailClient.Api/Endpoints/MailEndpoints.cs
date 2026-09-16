using MailClient.Api.Auth;
using MailClient.Application;
using MailClient.Application.Accounts;
using MailClient.Application.Mail;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Mail;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Services;
using MailClient.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Api.Endpoints;

public sealed record ReadRequest(bool IsRead);
public sealed record FolderOperationRequest(Guid FolderId);

public static class MailEndpoints
{
    public static void MapMailEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/mails", async (Guid? folderId, bool? isRead, bool? hasAttachments, string? search, int? page, int? pageSize, ICurrentMailAccount current, MailQueryService query, CancellationToken ct) =>
        {
            var result = await query.ListAsync(
                current.MailAccountId,
                new MailListRequest(folderId, isRead, hasAttachments, search, page ?? 1, pageSize ?? 0),
                ct);
            return Results.Ok(result);
        }).WithName("ListMails").WithSummary("List mailbox mail").WithDescription("Account-scoped list, newest first. Supports folderId, isRead, hasAttachments, search, page, pageSize (max 100).").Produces<MailListResponse>();
        api.MapGet("/mails/{id:guid}", async (Guid id, ICurrentMailAccount current, MailReadService reader, CancellationToken ct) => await reader.GetAsync(current.MailAccountId, id, ct) is { } mail ? Results.Ok(mail) : Results.NotFound()).WithName("GetMail").WithSummary("Get mailbox mail").Produces<MailDetailResponse>().Produces(404);
        api.MapPatch("/mails/{id:guid}/read", async (Guid id, ReadRequest request, ICurrentMailAccount current, CorrelationContext correlation, IMailOperationService operations, CancellationToken ct) =>
        {
            var result = await operations.ExecuteAsync(current.MailAccountId, new MailOperationRequest(id, request.IsRead ? MailOperationKind.Read : MailOperationKind.Unread), correlation.CorrelationId, ct);
            return OperationResult(result, correlation.CorrelationId, legacyRead: true);
        }).WithName("SetMailReadState").WithSummary("Change mail read state").Produces(204).Produces(404).ProducesProblem(409).ProducesProblem(502);

        MapOperation(api, "unread", MailOperationKind.Unread);
        MapOperation(api, "read", MailOperationKind.Read);
        MapOperation(api, "star", MailOperationKind.Star);
        MapOperation(api, "unstar", MailOperationKind.Unstar);
        MapOperation(api, "trash", MailOperationKind.Trash);
        MapOperation(api, "restore", MailOperationKind.Restore);
        MapOperation(api, "archive", MailOperationKind.Archive);
        MapOperation(api, "spam", MailOperationKind.Spam);
        MapOperation(api, "not-spam", MailOperationKind.NotSpam);
        api.MapPost("/mails/{id:guid}/move", async (Guid id, FolderOperationRequest request, ICurrentMailAccount current, CorrelationContext correlation, IMailOperationService operations, CancellationToken ct) => OperationResult(await operations.ExecuteAsync(current.MailAccountId, new MailOperationRequest(id, MailOperationKind.Move, request.FolderId), correlation.CorrelationId, ct), correlation.CorrelationId));
        api.MapPost("/mails/{id:guid}/copy", async (Guid id, FolderOperationRequest request, ICurrentMailAccount current, CorrelationContext correlation, IMailOperationService operations, CancellationToken ct) => OperationResult(await operations.ExecuteAsync(current.MailAccountId, new MailOperationRequest(id, MailOperationKind.Copy, request.FolderId), correlation.CorrelationId, ct), correlation.CorrelationId));
        api.MapGet("/mails/{mailId:guid}/attachments/{attachmentId:guid}", async (Guid mailId, Guid attachmentId, ICurrentMailAccount current, AppDbContext db, LocalAttachmentStorage storage, CancellationToken ct) =>
        {
            var attachment = await db.Attachments.SingleOrDefaultAsync(x => x.Id == attachmentId && x.MailId == mailId && x.MailAccountId == current.MailAccountId, ct);
            return attachment is null ? Results.NotFound() : Results.File(await storage.OpenReadAsync(attachment.StoragePath, ct), attachment.ContentType, attachment.FileName);
        }).WithName("DownloadAttachment").WithSummary("Download account-owned attachment").Produces(200).Produces(404);
        api.MapGet("/mails/{id:guid}/compose/reply", async (Guid id, ICurrentMailAccount current, ComposeContextService service, CancellationToken ct) => await ComposeResult(service.GetAsync(current.MailAccountId, id, ComposeMode.Reply, ct)));
        api.MapGet("/mails/{id:guid}/compose/reply-all", async (Guid id, ICurrentMailAccount current, ComposeContextService service, CancellationToken ct) => await ComposeResult(service.GetAsync(current.MailAccountId, id, ComposeMode.ReplyAll, ct)));
        api.MapGet("/mails/{id:guid}/compose/forward", async (Guid id, ICurrentMailAccount current, ComposeContextService service, CancellationToken ct) => await ComposeResult(service.GetAsync(current.MailAccountId, id, ComposeMode.Forward, ct)));
        api.MapPost("/mails/send", async (HttpRequest request, ICurrentMailAccount current, MailSendService sender, CancellationToken ct) =>
        {
            var key = request.Headers["Idempotency-Key"].ToString();
            var form = await request.ReadFormAsync(ct);
            static string? Optional(IFormCollection f, string name)
            {
                var value = f[name].ToString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }

            var attachments = new List<SendMailAttachment>();
            foreach (var file in form.Files)
                attachments.Add(new SendMailAttachment(file.FileName, file.ContentType, file.OpenReadStream()));
            var to = form["To"].Concat(form["to"]).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToList();
            var cc = form["Cc"].Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToList();
            var bcc = form["Bcc"].Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToList();
            Guid? replySourceMailId = Guid.TryParse(Optional(form, "replySourceMailId"), out var sourceMailId) ? sourceMailId : null;
            var command = new SendMailCommand(current.MailAccountId, to, cc, bcc, form["subject"].ToString(), Optional(form, "bodyHtml"), Optional(form, "bodyText"), attachments, replySourceMailId)
            {
                IdempotencyKey = key
            };
            var result = await sender.SendAsync(current.MailAccountId, command, request.HttpContext.RequestServices.GetRequiredService<CorrelationContext>().CorrelationId, ct);
            return Results.Ok(new { sent = result.Sent, sentCopySaved = result.SentCopySaved, warning = result.Warning });
        }).WithName("SendMail").WithSummary("Send mail idempotently").WithDescription("multipart/form-data: to, subject, bodyHtml and/or bodyText, up to 20 attachments. Idempotency-Key header is required.").Accepts<IFormCollection>("multipart/form-data").Produces(200).ProducesProblem(400).ProducesProblem(409).DisableAntiforgery();
    }

    private static async Task<IResult> ComposeResult(Task<ComposeContextResponse?> response) =>
        await response is { } context ? Results.Ok(context) : Results.NotFound();

    private static void MapOperation(RouteGroupBuilder api, string route, MailOperationKind kind) =>
        api.MapPost($"/mails/{{id:guid}}/{route}", async (Guid id, ICurrentMailAccount current, CorrelationContext correlation, IMailOperationService operations, CancellationToken ct) =>
            OperationResult(await operations.ExecuteAsync(current.MailAccountId, new MailOperationRequest(id, kind), correlation.CorrelationId, ct), correlation.CorrelationId));

    private static IResult OperationResult(MailOperationResult result, string correlationId, bool legacyRead = false)
    {
        if (result.Success)
            return Results.NoContent();
        var (status, title, code) = result.Error switch
        {
            MailOperationError.NotFound => (404, "Mail not found.", "mail_not_found"),
            MailOperationError.FolderNotFound => (404, "Mail folder not found.", "mail_folder_not_found"),
            MailOperationError.NeedsReauthentication => (409, "Mailbox needs reauthentication.", "mail_account_needs_reauthentication"),
            MailOperationError.ProviderUnavailable => (502, "Mail provider unavailable.", "mail_provider_unavailable"),
            MailOperationError.Conflict when legacyRead => (409, "Mailbox folder changed.", "mailbox_changed"),
            MailOperationError.Conflict => (409, "Mailbox state changed.", "mail_operation_conflict"),
            MailOperationError.MoveFailed => (502, "Mail move failed.", "mail_move_failed"),
            MailOperationError.NotSupported => (422, "Mail operation is not supported.", "mail_operation_not_supported"),
            _ => (500, "Mail operation failed.", "mail_operation_failed")
        };
        return Results.Problem(title: title, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code, ["correlationId"] = correlationId });
    }
}
