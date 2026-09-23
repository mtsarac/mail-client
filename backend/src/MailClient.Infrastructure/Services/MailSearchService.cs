using System.Diagnostics;
using MailClient.Application.Mail;
using MailClient.Application.Observability;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Runtime;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace MailClient.Infrastructure.Services;

public sealed class MailSearchService(AppDbContext db, RuntimeOperationSettings operationSettings, MailClientMetrics? metrics = null)
{
    private const int DefaultPageSize = 50;

    public async Task<MailListResponse> SearchAsync(Guid accountId, MailSearchRequest request, CancellationToken cancellationToken)
    {
        var hasTextQuery = !string.IsNullOrWhiteSpace(request.Query);
        using var activity = MailClientTelemetry.StartActivity("mailclient.mail.search");
        activity?.SetTag("mail.search.has_text_query", hasTextQuery);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var response = await SearchCoreAsync(accountId, request, cancellationToken);
            metrics?.RecordMailSearch(hasTextQuery, "success", Stopwatch.GetElapsedTime(started), response.Total);
            return response;
        }
        catch (OperationCanceledException)
        {
            metrics?.RecordMailSearch(hasTextQuery, "cancelled", Stopwatch.GetElapsedTime(started), null);
            throw;
        }
        catch (Exception ex)
        {
            MailClientTelemetry.MarkFailed(activity, ex);
            metrics?.RecordMailSearch(hasTextQuery, "failure", Stopwatch.GetElapsedTime(started), null);
            throw;
        }
    }

    private async Task<MailListResponse> SearchCoreAsync(Guid accountId, MailSearchRequest request, CancellationToken cancellationToken)
    {
        var settings = (await operationSettings.GetAsync(cancellationToken)).Settings.Search;
        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = request.PageSize < 1 ? DefaultPageSize : Math.Min(request.PageSize, settings.MaxPageSize);
        var queryText = request.Query?.Trim();
        if (queryText is { Length: var length } && length > settings.MaxQueryLength) queryText = queryText[..settings.MaxQueryLength];
        var query = db.Mails.AsNoTracking().Where(mail => mail.MailAccountId == accountId);
        if (request.FolderId is { } folderId) query = query.Where(mail => mail.MailFolderId == folderId);
        if (request.ConversationId is { } conversationId) query = query.Where(mail => mail.ConversationId == conversationId);
        if (request.IsRead is { } isRead) query = query.Where(mail => mail.IsRead == isRead);
        if (request.Flagged is { } flagged) query = query.Where(mail => mail.Flagged == flagged);
        if (request.HasAttachment is { } hasAttachment) query = query.Where(mail => mail.HasAttachments == hasAttachment);
        if (request.FromDate is { } fromDate)
        {
            var from = AsUtc(fromDate);
            query = query.Where(mail => mail.ReceivedAt >= from);
        }
        if (request.ToDate is { } toDate)
        {
            var to = AsUtc(toDate);
            query = query.Where(mail => mail.ReceivedAt < to);
        }
        if (!string.IsNullOrWhiteSpace(request.From))
        {
            var from = request.From.Trim().ToLowerInvariant();
            query = query.Where(mail => mail.FromAddress.ToLower().Contains(from) || mail.FromDisplayName.ToLower().Contains(from));
        }
        if (!string.IsNullOrWhiteSpace(request.To))
        {
            var to = request.To.Trim().ToLowerInvariant();
            query = query.Where(mail => mail.ToAddress.ToLower().Contains(to)
                || db.Participants.Any(participant => participant.MailId == mail.Id
                    && (participant.Type == ParticipantType.To || participant.Type == ParticipantType.Cc || participant.Type == ParticipantType.Bcc)
                    && (participant.Address.ToLower().Contains(to) || participant.DisplayName.ToLower().Contains(to))));
        }
        var useTs = !string.IsNullOrWhiteSpace(queryText) && db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL";
        if (useTs)
        {
            var plain = queryText!;
            // Contains (not LIKE with the raw text) so '%' and '_' in the query are matched literally.
            var lower = plain.ToLowerInvariant();
            query = query.Where(mail => EF.Property<NpgsqlTsVector>(mail, "SearchVector").Matches(EF.Functions.WebSearchToTsQuery("simple", plain))
                || db.Participants.Any(participant => participant.MailId == mail.Id && (participant.Address.ToLower().Contains(lower) || participant.DisplayName.ToLower().Contains(lower)))
                || db.Attachments.Any(attachment => attachment.MailId == mail.Id && attachment.FileName.ToLower().Contains(lower)));
        }
        else if (!string.IsNullOrWhiteSpace(queryText))
        {
            var normalized = queryText.ToLowerInvariant();
            query = query.Where(mail => mail.Subject.ToLower().Contains(normalized) || mail.BodyText.ToLower().Contains(normalized)
                || mail.FromAddress.ToLower().Contains(normalized) || mail.ToAddress.ToLower().Contains(normalized));
        }

        var total = await query.CountAsync(cancellationToken);
        var ordered = useTs
            ? query.OrderByDescending(mail => EF.Property<NpgsqlTsVector>(mail, "SearchVector").Rank(EF.Functions.WebSearchToTsQuery("simple", queryText!)))
                .ThenByDescending(mail => mail.ReceivedAt).ThenByDescending(mail => mail.Uid).ThenByDescending(mail => mail.Id)
            : query.OrderByDescending(mail => mail.ReceivedAt).ThenByDescending(mail => mail.Uid).ThenByDescending(mail => mail.Id);
        var skip = (int)Math.Min((long)(page - 1) * pageSize, int.MaxValue);
        var items = await ordered.Skip(skip).Take(pageSize)
            .Select(mail => new MailListItemResponse(mail.Id, mail.MailFolderId, mail.Subject, mail.FromAddress, mail.FromDisplayName, mail.ToAddress, mail.IsRead, mail.HasAttachments, mail.ReceivedAt, mail.ConversationId,
                mail.BodyText.Substring(0, Math.Min(mail.BodyText.Length, 120)), mail.Flagged, mail.Answered, mail.Attachments.Count))
            .ToListAsync(cancellationToken);
        return new(items, page, pageSize, total);
    }

    // Dates without an offset are UTC by contract; offsets are converted. PostgreSQL timestamptz accepts UTC only.
    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
