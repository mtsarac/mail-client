using MailClient.Api.Auth;
using MailClient.Api.OpenApi;
using MailClient.Application;
using MailClient.Application.Accounts;
using MailClient.Application.Mail;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Mail;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Services;
using MailClient.Infrastructure.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Api.Endpoints;

public sealed record ReadRequest(bool IsRead);
public sealed record FolderOperationRequest(Guid FolderId);
public sealed record SendMailResponse(bool Sent, bool SentCopySaved, string? Warning, Guid? MailId, Guid? ConversationId);
public sealed record BulkMailOperationRequest(IReadOnlyList<Guid> MailIds, Guid? FolderId = null);
public sealed record BulkMailOperationItemResponse(Guid MailId, bool Success, string? Code);
public sealed record BulkMailOperationResponse(IReadOnlyList<BulkMailOperationItemResponse> Results);

public static class MailEndpoints
{
    public static void MapMailEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        const string MailTag = "Mail", SearchTag = "Search", DraftsTag = "Drafts", OperationsTag = "Mail Operations", ComposeTag = "Compose";
        api.MapGet("/search", async (string? q, Guid? folderId, Guid? conversationId, string? from, string? to, DateTime? fromDate, DateTime? toDate, bool? isRead, bool? flagged, bool? hasAttachment, int? page, int? pageSize, ICurrentMailAccount current, MailSearchService search, CancellationToken ct) =>
        {
            var result = await search.SearchAsync(current.MailAccountId, new MailSearchRequest(q, folderId, conversationId, from, to, fromDate, toDate, isRead, flagged, hasAttachment, page ?? 1, pageSize ?? 0), ct);
            return Results.Ok(result);
        }).WithTags(SearchTag).WithName("SearchMails").WithSummary("Search account mailbox").WithDescription("Searches cached mail of the current mailbox. All filters are optional and combined with AND.").Produces<MailListResponse>();
        api.MapGet("/mails", async (Guid? folderId, bool? isRead, bool? hasAttachments, string? search, int? page, int? pageSize, ICurrentMailAccount current, MailSearchService searcher, CancellationToken ct) =>
            Results.Ok(await searcher.SearchAsync(
                current.MailAccountId,
                new MailSearchRequest(search, folderId, null, null, null, null, null, isRead, null, hasAttachments, page ?? 1, pageSize ?? 0),
                ct))).WithTags(MailTag).WithName("ListMails").WithSummary("List mailbox mail").WithDescription("Account-scoped list, newest first. Supports folderId, isRead, hasAttachments, search, page, pageSize (max 100).").Produces<MailListResponse>();
        api.MapGet("/mails/{id:guid}", async (Guid id, ICurrentMailAccount current, MailReadService reader, CancellationToken ct) => await reader.GetAsync(current.MailAccountId, id, ct) is { } mail ? Results.Ok(mail) : Results.NotFound()).WithTags(MailTag).WithName("GetMail").WithSummary("Get mailbox mail").WithDescription("Full mail with sanitized body and attachment metadata. `id` comes from list/search results.").Produces<MailDetailResponse>().Produces(404);
        api.MapGet("/drafts/{id:guid}", async (Guid id, ICurrentMailAccount current, DraftService drafts, CancellationToken ct) => await drafts.GetAsync(current.MailAccountId, id, ct) switch
        {
            { Draft: { } draft } => Results.Ok(draft),
            { Error: DraftLookupError.NotDraft } => Results.Problem(statusCode: 422, extensions: new Dictionary<string, object?> { ["code"] = "mail_not_draft" }),
            _ => Results.NotFound()
        }).WithTags(DraftsTag).WithName("GetDraft").WithSummary("Get draft").Produces<MailDetailResponse>().Produces(404).ProblemCodes(422, "mail_not_draft");
        api.MapPost("/drafts", async (IFormCollection form, ICurrentMailAccount current, DraftService drafts, CorrelationContext correlation, CancellationToken ct) =>
        {
            var command = ComposeForm.Read(form).ToDraft(current.MailAccountId);
            var result = await drafts.CreateAsync(current.MailAccountId, command, correlation.CorrelationId, ct);
            return Results.Ok(result);
        }).DisableAntiforgery().WithTags(DraftsTag).WithName("CreateDraft").WithSummary("Create draft")
            .WithDescription("multipart/form-data with the same fields as send (all optional). Saved to the mailbox Drafts folder.")
            .Accepts<IFormCollection>("multipart/form-data").Produces<DraftWriteResult>().ProblemCodes(404, "mail_account_not_found").ProblemCodes(422, "drafts_folder_unavailable");
        api.MapPut("/drafts/{id:guid}", async (Guid id, IFormCollection form, ICurrentMailAccount current, DraftService drafts, CorrelationContext correlation, CancellationToken ct) =>
        {
            var command = ComposeForm.Read(form).ToDraft(current.MailAccountId);
            return Results.Ok(await drafts.UpdateAsync(current.MailAccountId, id, command, correlation.CorrelationId, ct));
        }).DisableAntiforgery().WithTags(DraftsTag).WithName("UpdateDraft").WithSummary("Replace draft contents")
            .WithDescription("multipart/form-data, same fields as create. Replaces the draft (the returned mailId may differ from `id`).")
            .Accepts<IFormCollection>("multipart/form-data").Produces<DraftWriteResult>().ProblemCodes(404, "draft_not_found").ProblemCodes(422, "mail_not_draft", "drafts_folder_unavailable");
        api.MapDelete("/drafts/{id:guid}", async (Guid id, ICurrentMailAccount current, DraftService drafts, CorrelationContext correlation, CancellationToken ct) =>
        {
            await drafts.DeleteAsync(current.MailAccountId, id, correlation.CorrelationId, ct);
            return Results.NoContent();
        }).WithTags(DraftsTag).WithName("DeleteDraft").WithSummary("Delete draft").Produces(204).ProblemCodes(404, "draft_not_found").ProblemCodes(422, "trash_folder_unavailable", "mail_not_draft").ProblemCodes(502, "draft_delete_failed");
        api.MapPost("/drafts/{id:guid}/send", async (Guid id, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, ICurrentMailAccount current, DraftService drafts, CorrelationContext correlation, CancellationToken ct) =>
            Results.Ok(await drafts.SendAsync(current.MailAccountId, id, idempotencyKey ?? "", correlation.CorrelationId, ct)))
            .DisableAntiforgery().WithTags(DraftsTag).WithName("SendDraft").WithSummary("Send an existing draft")
            .WithDescription("Sends the draft and removes it. Requires the Idempotency-Key header; retrying a successful send with the same key replays the result.")
            .Produces<DraftSendResult>().ProblemCodes(400, "recipient_required", "body_required", "idempotency_key_required", "idempotency_key_too_long")
            .ProblemCodes(404, "draft_not_found").ProblemCodes(409, "idempotency_conflict", "send_in_progress", "delivery_unknown").ProblemCodes(422, "mail_not_draft");

        api.MapPatch("/mails/{id:guid}/read", async (Guid id, ReadRequest request, ICurrentMailAccount current, CorrelationContext correlation, IMailOperationService operations, CancellationToken ct) =>
        {
            var result = await operations.ExecuteAsync(current.MailAccountId, new MailOperationRequest(id, request.IsRead ? MailOperationKind.Read : MailOperationKind.Unread), correlation.CorrelationId, ct);
            return OperationResult(result, correlation.CorrelationId, legacyRead: true);
        }).WithTags(OperationsTag).WithName("SetMailReadState").WithSummary("Set read state (PATCH)").WithDescription("Body {\"isRead\": true|false}. Equivalent to POST /read or /unread; legacy conflict code is mailbox_changed.")
            .Produces(204).ProblemCodes(404, "mail_not_found").ProblemCodes(409, "mailbox_changed", "mail_account_needs_reauthentication").ProblemCodes(502, "mail_provider_unavailable");

        MapOperation(api, "unread", MailOperationKind.Unread, "MarkMailUnread", "Mark unread");
        MapOperation(api, "read", MailOperationKind.Read, "MarkMailRead", "Mark read");
        MapOperation(api, "star", MailOperationKind.Star, "StarMail", "Star (flag) mail");
        MapOperation(api, "unstar", MailOperationKind.Unstar, "UnstarMail", "Remove star");
        MapOperation(api, "trash", MailOperationKind.Trash, "TrashMail", "Move to Trash");
        MapOperation(api, "restore", MailOperationKind.Restore, "RestoreMail", "Restore from Trash");
        MapOperation(api, "archive", MailOperationKind.Archive, "ArchiveMail", "Move to Archive");
        MapOperation(api, "spam", MailOperationKind.Spam, "MarkMailSpam", "Move to Junk/Spam");
        MapOperation(api, "not-spam", MailOperationKind.NotSpam, "MarkMailNotSpam", "Move out of Junk/Spam");
        MapOperation(api, "delete", MailOperationKind.Delete, "DeleteMailPermanently", "Permanently delete from Trash/Junk",
            "Remote-first: expunges only this message on the mail server, then removes it and its attachments locally. Allowed only for mail in the Trash or Junk folder (otherwise 422 mail_operation_not_supported). Cannot be undone. No request body.");
        api.MapPost("/mails/{id:guid}/move", async (Guid id, FolderOperationRequest request, ICurrentMailAccount current, CorrelationContext correlation, IMailOperationService operations, CancellationToken ct) => OperationResult(await operations.ExecuteAsync(current.MailAccountId, new MailOperationRequest(id, MailOperationKind.Move, request.FolderId), correlation.CorrelationId, ct), correlation.CorrelationId)).WithTags(OperationsTag).WithName("MoveMail").WithSummary("Move mail to a folder").WithDescription("Moves the mail to the folder in `folderId` (remote-first).").WithOperationProblems();
        api.MapPost("/mails/{id:guid}/copy", async (Guid id, FolderOperationRequest request, ICurrentMailAccount current, CorrelationContext correlation, IMailOperationService operations, CancellationToken ct) => OperationResult(await operations.ExecuteAsync(current.MailAccountId, new MailOperationRequest(id, MailOperationKind.Copy, request.FolderId), correlation.CorrelationId, ct), correlation.CorrelationId)).WithTags(OperationsTag).WithName("CopyMail").WithSummary("Copy mail to a folder").WithDescription("Copies the mail into the folder in `folderId`.").WithOperationProblems();
        api.MapPost("/mails/bulk/{action}", async (string action, BulkMailOperationRequest request, ICurrentMailAccount current, CorrelationContext correlation, IMailOperationService operations, CancellationToken ct) =>
        {
            if (ParseBulkAction(action) is not { } kind)
                return Results.NotFound();
            if (request.MailIds is not { Count: > 0 })
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["mailIds"] = ["At least one mail id is required."] });
            if (request.MailIds.Count > MaxBulkOperationSize)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["mailIds"] = [$"At most {MaxBulkOperationSize} mail ids are allowed per request."] });
            if (kind == MailOperationKind.Move && request.FolderId is null)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["folderId"] = ["folderId is required for move."] });

            var result = await operations.ExecuteBulkAsync(current.MailAccountId, request.MailIds, kind, request.FolderId, correlation.CorrelationId, ct);
            return Results.Ok(new BulkMailOperationResponse(result.Results
                .Select(item => new BulkMailOperationItemResponse(item.MailId, item.Success, item.Success ? null : MapOperationError(item.Error).Code))
                .ToList()));
        }).WithTags(OperationsTag).WithName("BulkMailOperation").WithSummary("Apply a mail operation to multiple mails")
            .WithDescription("action: read, unread, star, unstar, archive, trash, restore, spam, not-spam, delete (Trash/Junk only, permanent), or move (move requires folderId). 1-100 ids. Each mail is applied independently, so one failure does not block the rest of the batch — always 200 for a valid request; check per-item `success`/`code`.")
            .Produces<BulkMailOperationResponse>().ProducesValidationProblem().Produces(404);
        api.MapGet("/mails/{mailId:guid}/attachments/{attachmentId:guid}", async (Guid mailId, Guid attachmentId, ICurrentMailAccount current, AppDbContext db, IFileStorage storage, CancellationToken ct) =>
        {
            var attachment = await db.Attachments.SingleOrDefaultAsync(x => x.Id == attachmentId && x.MailId == mailId && x.MailAccountId == current.MailAccountId, ct);
            return attachment is null ? Results.NotFound() : Results.File(await storage.OpenReadAsync(attachment.StoragePath, ct), attachment.ContentType, attachment.FileName);
        }).WithTags(MailTag).WithName("DownloadAttachment").WithSummary("Download account-owned attachment").Produces(200, contentType: "application/octet-stream").Produces(404);
        api.MapGet("/mails/{id:guid}/compose/reply", async (Guid id, ICurrentMailAccount current, ComposeContextService service, CancellationToken ct) => await ComposeResult(service.GetAsync(current.MailAccountId, id, ComposeMode.Reply, ct))).WithTags(ComposeTag).WithName("GetReplyContext").WithSummary("Reply compose context").WithDescription("Prefilled recipients, subject and quoted body. Send the result via POST /api/mails/send with replySourceMailId set to this mail id.").Produces<ComposeContextResponse>().Produces(404);
        api.MapGet("/mails/{id:guid}/compose/reply-all", async (Guid id, ICurrentMailAccount current, ComposeContextService service, CancellationToken ct) => await ComposeResult(service.GetAsync(current.MailAccountId, id, ComposeMode.ReplyAll, ct))).WithTags(ComposeTag).WithName("GetReplyAllContext").WithSummary("Reply-all compose context").WithDescription("Prefilled recipients, subject and quoted body. Send the result via POST /api/mails/send with replySourceMailId set to this mail id.").Produces<ComposeContextResponse>().Produces(404);
        api.MapGet("/mails/{id:guid}/compose/forward", async (Guid id, ICurrentMailAccount current, ComposeContextService service, CancellationToken ct) => await ComposeResult(service.GetAsync(current.MailAccountId, id, ComposeMode.Forward, ct))).WithTags(ComposeTag).WithName("GetForwardContext").WithSummary("Forward compose context").WithDescription("Prefilled recipients, subject and quoted body. Send the result via POST /api/mails/send with replySourceMailId set to this mail id.").Produces<ComposeContextResponse>().Produces(404);
        api.MapPost("/mails/send", async (IFormCollection form, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, ICurrentMailAccount current, MailSendService sender, CorrelationContext correlation, CancellationToken ct) =>
        {
            var command = ComposeForm.Read(form).ToSend(current.MailAccountId, idempotencyKey ?? "");
            var result = await sender.SendAsync(current.MailAccountId, command, correlation.CorrelationId, ct);
            return Results.Ok(new SendMailResponse(result.Sent, result.SentCopySaved, result.Warning, result.MailId, result.ConversationId));
        }).WithTags(ComposeTag).WithName("SendMail").WithSummary("Send mail idempotently").WithDescription("multipart/form-data: to, subject, bodyHtml and/or bodyText, up to 20 attachments. Idempotency-Key header is required; retrying with the same key never sends twice.")
            .Accepts<IFormCollection>("multipart/form-data").Produces<SendMailResponse>()
            .ProblemCodes(400, "recipient_required", "invalid_recipient", "body_required", "body_too_large", "too_many_attachments", "attachment_too_large", "idempotency_key_required", "idempotency_key_too_long")
            .ProblemCodes(401, "mail_smtp_authentication_failed").ProblemCodes(409, "idempotency_conflict", "send_in_progress", "delivery_unknown", "mail_account_needs_reauthentication")
            .ProblemCodes(502, "mail_provider_unavailable", "mail_server_unreachable", "mail_tls_failed").DisableAntiforgery();
    }

    /// <summary>Shared multipart/form-data shape of send, create-draft and update-draft. Form keys are case-insensitive.</summary>
    private sealed record ComposeForm(
        IReadOnlyList<string> To,
        IReadOnlyList<string> Cc,
        IReadOnlyList<string> Bcc,
        string Subject,
        string? BodyHtml,
        string? BodyText,
        IReadOnlyList<SendMailAttachment> Attachments,
        Guid? ReplySourceMailId)
    {
        public static ComposeForm Read(IFormCollection form) => new(
            Values(form, "to"),
            Values(form, "cc"),
            Values(form, "bcc"),
            form["subject"].ToString(),
            Optional(form, "bodyHtml"),
            Optional(form, "bodyText"),
            form.Files.Select(file => new SendMailAttachment(file.FileName, file.ContentType, file.OpenReadStream())).ToList(),
            Guid.TryParse(Optional(form, "replySourceMailId"), out var sourceMailId) ? sourceMailId : null);

        public DraftCommand ToDraft(Guid accountId) =>
            new(accountId, To, Cc, Bcc, Subject, BodyHtml, BodyText, Attachments, ReplySourceMailId);

        public SendMailCommand ToSend(Guid accountId, string idempotencyKey) =>
            new(accountId, To, Cc, Bcc, Subject, BodyHtml, BodyText, Attachments, ReplySourceMailId) { IdempotencyKey = idempotencyKey };

        private static List<string> Values(IFormCollection form, string name) =>
            form[name].Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToList();

        private static string? Optional(IFormCollection form, string name)
        {
            var value = form[name].ToString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }

    private static async Task<IResult> ComposeResult(Task<ComposeContextResponse?> response) =>
        await response is { } context ? Results.Ok(context) : Results.NotFound();

    private const int MaxBulkOperationSize = 100;

    private static MailOperationKind? ParseBulkAction(string action) => action switch
    {
        "read" => MailOperationKind.Read,
        "unread" => MailOperationKind.Unread,
        "archive" => MailOperationKind.Archive,
        "trash" => MailOperationKind.Trash,
        "move" => MailOperationKind.Move,
        "star" => MailOperationKind.Star,
        "unstar" => MailOperationKind.Unstar,
        "spam" => MailOperationKind.Spam,
        "not-spam" => MailOperationKind.NotSpam,
        "restore" => MailOperationKind.Restore,
        "delete" => MailOperationKind.Delete,
        _ => null
    };

    private static void MapOperation(RouteGroupBuilder api, string route, MailOperationKind kind, string name, string summary,
        string description = "Remote-first: applied on the mail server, then mirrored locally. No request body.") =>
        api.MapPost($"/mails/{{id:guid}}/{route}", async (Guid id, ICurrentMailAccount current, CorrelationContext correlation, IMailOperationService operations, CancellationToken ct) =>
            OperationResult(await operations.ExecuteAsync(current.MailAccountId, new MailOperationRequest(id, kind), correlation.CorrelationId, ct), correlation.CorrelationId))
            .WithTags("Mail Operations").WithName(name).WithSummary(summary).WithDescription(description).WithOperationProblems(kind);

    private static RouteHandlerBuilder WithOperationProblems(this RouteHandlerBuilder builder, MailOperationKind? kind = null)
    {
        // Delete never resolves a destination folder and reports its own server-side failure code.
        var delete = kind == MailOperationKind.Delete;
        return builder
            .Produces(204)
            .ProblemCodes(404, delete ? ["mail_not_found"] : ["mail_not_found", "mail_folder_not_found"])
            .ProblemCodes(409, "mail_account_needs_reauthentication", "mail_operation_conflict")
            .ProblemCodes(422, "mail_operation_not_supported")
            .ProblemCodes(502, "mail_provider_unavailable", delete ? "mail_delete_failed" : "mail_move_failed");
    }

    private static IResult OperationResult(MailOperationResult result, string correlationId, bool legacyRead = false)
    {
        if (result.Success)
            return Results.NoContent();
        var (status, title, code) = MapOperationError(result.Error, legacyRead);
        return Results.Problem(title: title, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code, ["correlationId"] = correlationId });
    }

    private static (int Status, string Title, string Code) MapOperationError(MailOperationError error, bool legacyRead = false) => error switch
    {
        MailOperationError.NotFound => (404, "Mail not found.", "mail_not_found"),
        MailOperationError.FolderNotFound => (404, "Mail folder not found.", "mail_folder_not_found"),
        MailOperationError.NeedsReauthentication => (409, "Mailbox needs reauthentication.", "mail_account_needs_reauthentication"),
        MailOperationError.ProviderUnavailable => (502, "Mail provider unavailable.", "mail_provider_unavailable"),
        MailOperationError.Conflict when legacyRead => (409, "Mailbox folder changed.", "mailbox_changed"),
        MailOperationError.Conflict => (409, "Mailbox state changed.", "mail_operation_conflict"),
        MailOperationError.MoveFailed => (502, "Mail move failed.", "mail_move_failed"),
        MailOperationError.DeleteFailed => (502, "Mail delete failed.", "mail_delete_failed"),
        MailOperationError.NotSupported => (422, "Mail operation is not supported.", "mail_operation_not_supported"),
        _ => (500, "Mail operation failed.", "mail_operation_failed")
    };
}
