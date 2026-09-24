using System.Globalization;
using MailClient.Api.OpenApi;
using MailClient.Application;
using MailClient.Application.Accounts;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;

namespace MailClient.Api.Endpoints;

public sealed record ScheduledSendResponse(Guid Id, DateTime SendAtUtc, ScheduledSendStatus Status);

public sealed record ScheduledSendListItemResponse(
    Guid Id,
    IReadOnlyList<string> To,
    IReadOnlyList<string> Cc,
    IReadOnlyList<string> Bcc,
    string Subject,
    DateTime SendAtUtc,
    ScheduledSendStatus Status,
    DateTime CreatedAtUtc,
    Guid? SentMailId,
    string? FailureReason,
    int AttemptCount,
    DateTime? NextAttemptAtUtc);

public sealed record ScheduledSendListResponse(IReadOnlyList<ScheduledSendListItemResponse> Items);

public static class ScheduledSendEndpoints
{
    public static void MapScheduledSendEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization().WithTags("Scheduled Sends");

        api.MapPost("/scheduled-sends", async (
                IFormCollection form,
                [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
                ICurrentMailAccount current,
                ScheduledSendService scheduledSends,
                CorrelationContext correlation,
                CancellationToken ct) =>
            {
                if (!DateTime.TryParse(form["sendAtUtc"].ToString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var sendAtUtc))
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["sendAtUtc"] = ["sendAtUtc is required and must be an ISO-8601 date-time."]
                    });

                var command = MailEndpoints.ComposeForm.Read(form).ToSend(current.MailAccountId, idempotencyKey ?? "");
                var result = await scheduledSends.CreateAsync(
                    current.MailAccountId, command, DateTime.SpecifyKind(sendAtUtc, DateTimeKind.Utc), correlation.CorrelationId, ct);
                return Results.Created(
                    $"/api/scheduled-sends/{result.Id}",
                    new ScheduledSendResponse(result.Id, result.SendAtUtc, result.Status));
            })
            .DisableAntiforgery().WithName("CreateScheduledSend").WithSummary("Schedule a mail to send later")
            .WithDescription("multipart/form-data: same fields as POST /api/mails/send, plus sendAtUtc (ISO-8601, required, must be in the future). Idempotency-Key header is required; retrying with the same key never schedules twice.")
            .Accepts<IFormCollection>("multipart/form-data").Produces<ScheduledSendResponse>(201).ProducesValidationProblem()
            .ProblemCodes(400, "scheduled_send_in_past", "recipient_required", "invalid_recipient", "body_required", "body_too_large",
                "too_many_attachments", "attachment_too_large", "idempotency_key_required", "idempotency_key_too_long")
            .ProblemCodes(404, "mail_account_not_found").ProblemCodes(409, "idempotency_conflict");

        api.MapGet("/scheduled-sends", async (ICurrentMailAccount current, ScheduledSendService scheduledSends, CancellationToken ct) =>
            {
                var items = await scheduledSends.ListAsync(current.MailAccountId, ct);
                return Results.Ok(new ScheduledSendListResponse(items
                    .Select(item => new ScheduledSendListItemResponse(
                        item.Id, item.To, item.Cc, item.Bcc, item.Subject, item.SendAtUtc, item.Status,
                        item.CreatedAtUtc, item.SentMailId, item.FailureReason, item.AttemptCount, item.NextAttemptAtUtc))
                    .ToList()));
            })
            .WithName("ListScheduledSends").WithSummary("List scheduled sends")
            .WithDescription("Account-scoped, no pagination, ordered by sendAtUtc ascending.")
            .Produces<ScheduledSendListResponse>();

        api.MapGet("/scheduled-sends/{id:guid}", async (Guid id, ICurrentMailAccount current,
                ScheduledSendService scheduledSends, CancellationToken ct) =>
                Results.Ok(await scheduledSends.GetAsync(current.MailAccountId, id, ct)))
            .WithName("GetScheduledSend").WithSummary("Inspect a scheduled send and its staged attachment metadata")
            .Produces<ScheduledSendDetail>().ProblemCodes(404, "scheduled_send_not_found");

        api.MapPost("/scheduled-sends/{id:guid}/reschedule", async (Guid id, RescheduleFailedSend request,
                [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
                ICurrentMailAccount current, ScheduledSendService scheduledSends, CorrelationContext correlation,
                CancellationToken ct) =>
            {
                var result = await scheduledSends.RescheduleFailedAsync(current.MailAccountId, id, request,
                    idempotencyKey ?? "", correlation.CorrelationId, ct);
                return Results.Created($"/api/scheduled-sends/{result.Id}",
                    new ScheduledSendResponse(result.Id, result.SendAtUtc, result.Status));
            })
            .WithName("RescheduleFailedSend").WithSummary("Edit and explicitly reschedule a failed send")
            .WithDescription("JSON: sendAtUtc, to, cc, bcc, subject, bodyHtml, bodyText; attachmentIds is optional (omitted preserves all staged attachments). Requires a NEW Idempotency-Key. Delivery-unknown sends cannot be rescheduled.")
            .Produces<ScheduledSendResponse>(201)
            .ProblemCodes(400, "scheduled_send_in_past", "recipient_required", "invalid_recipient", "body_required",
                "body_too_large", "too_many_attachments", "attachment_too_large", "idempotency_key_required",
                "idempotency_key_too_long")
            .ProblemCodes(404, "scheduled_send_not_found", "scheduled_send_attachment_not_found")
            .ProblemCodes(409, "scheduled_send_already_sent", "idempotency_conflict");

        api.MapDelete("/scheduled-sends/{id:guid}", async (Guid id, ICurrentMailAccount current, ScheduledSendService scheduledSends, CancellationToken ct) =>
            {
                await scheduledSends.CancelAsync(current.MailAccountId, id, ct);
                return Results.NoContent();
            })
            .WithName("CancelScheduledSend").WithSummary("Discard a pending or failed scheduled send")
            .WithDescription("Cancels a pending send or discards failed content and its staged attachments. Delivery-unknown sends cannot be cancelled.")
            .Produces(204).ProblemCodes(404, "scheduled_send_not_found").ProblemCodes(409, "scheduled_send_already_sent");
    }
}
