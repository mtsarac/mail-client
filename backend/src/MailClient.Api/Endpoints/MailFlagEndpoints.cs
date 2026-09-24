using MailClient.Application.Accounts;
using MailClient.Domain.Entities;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Api.Endpoints;

public sealed record SnoozeRequest(DateTime UntilUtc);
public sealed record SnoozeMapResponse(IReadOnlyDictionary<Guid, DateTime> Snoozed);
public sealed record PinnedIdsResponse(IReadOnlyList<Guid> MailIds);
public sealed record SetPinnedRequest(IReadOnlyList<Guid> MailIds, bool Pinned);
public sealed record SetPinnedItemResponse(Guid MailId, bool Success, string? Code);
public sealed record SetPinnedResponse(IReadOnlyList<SetPinnedItemResponse> Results);

/// <summary>
/// Snooze and pin are app-only concepts with no IMAP equivalent, so — unlike read/star/archive — they're
/// stored directly here rather than routed through <c>IMailOperationService</c>.
/// </summary>
public static class MailFlagEndpoints
{
    private const int MaxPinnedPerAccount = 3;

    public static void MapMailFlagEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization().WithTags("Mail Flags");

        api.MapPut("/mails/{id:guid}/snooze", async (Guid id, SnoozeRequest request, ICurrentMailAccount current, AppDbContext db, CancellationToken ct) =>
        {
            if (!await db.Mails.AnyAsync(x => x.Id == id && x.MailAccountId == current.MailAccountId, ct))
                return Results.NotFound();
            var existing = await db.MailSnoozes.SingleOrDefaultAsync(x => x.MailAccountId == current.MailAccountId && x.MailId == id, ct);
            if (existing is null)
            {
                db.MailSnoozes.Add(new MailSnooze { Id = Guid.NewGuid(), MailAccountId = current.MailAccountId, MailId = id, UntilUtc = request.UntilUtc });
            }
            else
            {
                existing.UntilUtc = request.UntilUtc;
            }
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithName("SnoozeMail").WithSummary("Snooze a mail until a UTC deadline").Produces(204).Produces(404);

        api.MapDelete("/mails/{id:guid}/snooze", async (Guid id, ICurrentMailAccount current, AppDbContext db, CancellationToken ct) =>
        {
            var existing = await db.MailSnoozes.SingleOrDefaultAsync(x => x.MailAccountId == current.MailAccountId && x.MailId == id, ct);
            if (existing is not null)
            {
                db.MailSnoozes.Remove(existing);
                await db.SaveChangesAsync(ct);
            }
            return Results.NoContent();
        }).WithName("UnsnoozeMail").WithSummary("Clear a mail's snooze").WithDescription("Idempotent — clearing an already-unsnoozed mail also answers 204.").Produces(204);

        api.MapGet("/mails/snoozed", async (ICurrentMailAccount current, AppDbContext db, CancellationToken ct) =>
            Results.Ok(new SnoozeMapResponse(await db.MailSnoozes.AsNoTracking()
                .Where(x => x.MailAccountId == current.MailAccountId)
                .ToDictionaryAsync(x => x.MailId, x => x.UntilUtc, ct))))
            .WithName("GetSnoozedMails").WithSummary("Full mail-id to snooze-deadline map for the current mailbox").Produces<SnoozeMapResponse>();

        api.MapGet("/mails/pinned", async (ICurrentMailAccount current, AppDbContext db, CancellationToken ct) =>
            Results.Ok(new PinnedIdsResponse(await db.PinnedMails.AsNoTracking()
                .Where(x => x.MailAccountId == current.MailAccountId)
                .Select(x => x.MailId)
                .ToListAsync(ct))))
            .WithName("GetPinnedMails").WithSummary("Every pinned mail id for the current mailbox").Produces<PinnedIdsResponse>();

        api.MapPost("/mails/pinned", async (SetPinnedRequest request, ICurrentMailAccount current, AppDbContext db, CancellationToken ct) =>
        {
            var results = new List<SetPinnedItemResponse>();
            if (request.MailIds.Count == 0) return Results.Ok(new SetPinnedResponse(results));

            var ownedMailIds = (await db.Mails.Where(x => x.MailAccountId == current.MailAccountId && request.MailIds.Contains(x.Id)).Select(x => x.Id).ToListAsync(ct)).ToHashSet();
            var existing = await db.PinnedMails.Where(x => x.MailAccountId == current.MailAccountId).ToListAsync(ct);
            var pinnedIds = existing.Select(x => x.MailId).ToHashSet();

            if (request.Pinned)
            {
                var freeSlots = MaxPinnedPerAccount - pinnedIds.Count;
                foreach (var mailId in request.MailIds)
                {
                    if (!ownedMailIds.Contains(mailId)) { results.Add(new SetPinnedItemResponse(mailId, false, "mail_not_found")); continue; }
                    if (pinnedIds.Contains(mailId)) { results.Add(new SetPinnedItemResponse(mailId, true, null)); continue; }
                    if (freeSlots <= 0) { results.Add(new SetPinnedItemResponse(mailId, false, "pinned_limit_reached")); continue; }
                    db.PinnedMails.Add(new PinnedMail { Id = Guid.NewGuid(), MailAccountId = current.MailAccountId, MailId = mailId, PinnedAtUtc = DateTime.UtcNow });
                    pinnedIds.Add(mailId);
                    freeSlots--;
                    results.Add(new SetPinnedItemResponse(mailId, true, null));
                }
            }
            else
            {
                var byMailId = existing.ToDictionary(x => x.MailId);
                foreach (var mailId in request.MailIds)
                {
                    if (byMailId.TryGetValue(mailId, out var row)) db.PinnedMails.Remove(row);
                    results.Add(new SetPinnedItemResponse(mailId, true, null));
                }
            }
            await db.SaveChangesAsync(ct);
            return Results.Ok(new SetPinnedResponse(results));
        }).WithName("SetPinnedMails").WithSummary("Pin or unpin mails")
            .WithDescription($"Cap of {MaxPinnedPerAccount} pinned mails per account, enforced server-side. Each mail is applied independently — check per-item success/code rather than assuming the whole batch succeeded.")
            .Produces<SetPinnedResponse>();
    }
}
