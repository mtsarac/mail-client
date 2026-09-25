using MailClient.Application;
using MailClient.Api.OpenApi;
using MailClient.Application.Accounts;
using MailClient.Domain;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Observability;
using MailClient.Infrastructure.Services;

namespace MailClient.Api.Endpoints;

public sealed record ReplyReminderRequest(DateTime DueAtUtc);
public sealed record ReplyReminderResponse(
    Guid Id,
    Guid MailId,
    Guid? ConversationId,
    DateTime DueAtUtc,
    DateTime CreatedAt,
    ReplyReminderStatus Status,
    DateTime? NotifiedAt,
    string Subject,
    string Recipient,
    DateTime SentAt);
public sealed record ReplyReminderListResponse(IReadOnlyList<ReplyReminderResponse> Items);

public static class ReplyReminderEndpoints
{
    public static void MapReplyReminderEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization().WithTags("Reply Reminders");

        api.MapPost("/mails/{id:guid}/reply-reminder", async (
            Guid id,
            ReplyReminderRequest request,
            ICurrentMailAccount current,
            ReplyReminderService reminders,
            AuditLogger audit,
            CorrelationContext correlation,
            CancellationToken ct) =>
        {
            try
            {
                var item = await reminders.SetAsync(current.MailAccountId, id, request.DueAtUtc, ct);
                await audit.WriteAsync(current.MailAccountId, AuditActions.ReplyReminderCreated,
                    "ReplyReminder", item.Id.ToString(), null, correlation.CorrelationId, ct);
                return Results.Ok(ToResponse(item));
            }
            catch (InvalidOperationException ex) when (StatusFor(ex.Message) is { } status)
            {
                return Results.Problem(statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = ex.Message });
            }
        }).WithName("SetReplyReminder").WithSummary("Remind if a sent mail remains unanswered")
            .WithDescription("Creates or reschedules the account-scoped reminder for this sent mail. dueAtUtc must be in the future.")
            .Produces<ReplyReminderResponse>().ProblemCodes(400, "reply_reminder_in_past")
            .ProblemCodes(404, "mail_not_found", "mail_account_not_found")
            .ProblemCodes(409, "reply_reminder_already_replied")
            .ProblemCodes(422, "reply_reminder_requires_sent_mail");

        api.MapDelete("/mails/{id:guid}/reply-reminder", async (
            Guid id,
            ICurrentMailAccount current,
            ReplyReminderService reminders,
            AuditLogger audit,
            CorrelationContext correlation,
            CancellationToken ct) =>
        {
            if (await reminders.CancelAsync(current.MailAccountId, id, ct))
            {
                await audit.WriteAsync(current.MailAccountId, AuditActions.ReplyReminderCancelled,
                    "Mail", id.ToString(), null, correlation.CorrelationId, ct);
            }
            return Results.NoContent();
        }).WithName("CancelReplyReminder").WithSummary("Cancel a sent mail reply reminder")
            .WithDescription("Idempotent and account-scoped; an absent or foreign reminder also returns 204.").Produces(204);

        api.MapGet("/reply-reminders", async (
            ICurrentMailAccount current,
            ReplyReminderService reminders,
            CancellationToken ct) =>
            Results.Ok(new ReplyReminderListResponse(
                (await reminders.ListAsync(current.MailAccountId, ct)).Select(ToResponse).ToList())))
            .WithName("ListReplyReminders").WithSummary("List pending reply reminders")
            .WithDescription("Account-scoped, ordered by dueAtUtc ascending, no pagination.")
            .Produces<ReplyReminderListResponse>();
    }

    private static int? StatusFor(string code) => code switch
    {
        "reply_reminder_in_past" => 400,
        "mail_not_found" or "mail_account_not_found" => 404,
        "reply_reminder_already_replied" => 409,
        "reply_reminder_requires_sent_mail" => 422,
        _ => null
    };

    private static ReplyReminderResponse ToResponse(ReplyReminderItem item) => new(
        item.Id,
        item.MailId,
        item.ConversationId,
        item.DueAtUtc,
        item.CreatedAt,
        item.Status,
        item.NotifiedAt,
        item.Subject,
        item.Recipient,
        item.SentAt);
}
