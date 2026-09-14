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
        api.MapGet("/mails/{id:guid}", async (Guid id, ICurrentMailAccount current, MailReadService reader, CancellationToken ct) => await reader.GetAsync(current.MailAccountId, id, ct) is { } mail ? Results.Ok(mail) : Results.NotFound()).WithName("GetMail").WithSummary("Get mailbox mail").Produces<Mail>().Produces(404);
        api.MapPatch("/mails/{id:guid}/read", async (Guid id, ReadRequest request, ICurrentMailAccount current, AppDbContext db, MailReadService reader, CorrelationContext correlation, CancellationToken ct) =>
        {
            var status = await db.MailAccounts.Where(x => x.Id == current.MailAccountId).Select(x => x.Status).SingleOrDefaultAsync(ct);
            if (status == MailAccountStatus.NeedsReauthentication)
                return Results.Problem(title: "Mailbox needs reauthentication.", statusCode: 409, extensions: new Dictionary<string, object?> { ["code"] = "mail_account_needs_reauthentication", ["correlationId"] = correlation.CorrelationId });
            var outcome = await reader.SetReadAsync(current.MailAccountId, id, request.IsRead, correlation.CorrelationId, ct);
            if (!outcome.Found) return Results.NotFound();
            if (outcome.Conflict) return Results.Problem(title: "Mailbox folder changed.", statusCode: 409, extensions: new Dictionary<string, object?> { ["code"] = "mailbox_changed", ["correlationId"] = correlation.CorrelationId });
            if (outcome.ProviderError) return Results.Problem(title: "Mail provider unavailable.", statusCode: 502, extensions: new Dictionary<string, object?> { ["code"] = "mail_provider_unavailable", ["correlationId"] = correlation.CorrelationId });
            return Results.NoContent();
        }).WithName("SetMailReadState").WithSummary("Change mail read state").Produces(204).Produces(404).ProducesProblem(409).ProducesProblem(502);
        api.MapGet("/mails/{mailId:guid}/attachments/{attachmentId:guid}", async (Guid mailId, Guid attachmentId, ICurrentMailAccount current, AppDbContext db, LocalAttachmentStorage storage, CancellationToken ct) =>
        {
            var attachment = await db.Attachments.SingleOrDefaultAsync(x => x.Id == attachmentId && x.MailId == mailId && x.MailAccountId == current.MailAccountId, ct);
            return attachment is null ? Results.NotFound() : Results.File(await storage.OpenReadAsync(attachment.StoragePath, ct), attachment.ContentType, attachment.FileName);
        }).WithName("DownloadAttachment").WithSummary("Download account-owned attachment").Produces(200).Produces(404);
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
            var command = new SendMailCommand(current.MailAccountId, form["to"].ToString(), form["subject"].ToString(), Optional(form, "bodyHtml"), Optional(form, "bodyText"), attachments)
            {
                IdempotencyKey = key
            };
            var result = await sender.SendAsync(current.MailAccountId, command, request.HttpContext.RequestServices.GetRequiredService<CorrelationContext>().CorrelationId, ct);
            return Results.Ok(new { sent = result.Sent, sentCopySaved = result.SentCopySaved, warning = result.Warning });
        }).WithName("SendMail").WithSummary("Send mail idempotently").WithDescription("multipart/form-data: to, subject, bodyHtml and/or bodyText, up to 20 attachments. Idempotency-Key header is required.").Accepts<IFormCollection>("multipart/form-data").Produces(200).ProducesProblem(400).ProducesProblem(409).DisableAntiforgery();
    }
}
