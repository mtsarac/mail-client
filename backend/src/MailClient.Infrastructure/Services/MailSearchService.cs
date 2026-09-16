using MailClient.Application.Mail;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace MailClient.Infrastructure.Services;

public sealed class MailSearchService(AppDbContext db)
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 100;
    private const int MaxQueryLength = 200;

    public async Task<MailListResponse> SearchAsync(Guid accountId, MailSearchRequest request, CancellationToken cancellationToken)
    {
        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = request.PageSize < 1 ? DefaultPageSize : Math.Min(request.PageSize, MaxPageSize);
        var queryText = request.Query?.Trim();
        if (queryText is { Length: > MaxQueryLength }) queryText = queryText[..MaxQueryLength];
        var query = db.Mails.AsNoTracking().Where(mail => mail.MailAccountId == accountId);
        if (request.FolderId is { } folderId) query = query.Where(mail => mail.MailFolderId == folderId);
        if (request.ConversationId is { } conversationId) query = query.Where(mail => mail.ConversationId == conversationId);
        if (request.IsRead is { } isRead) query = query.Where(mail => mail.IsRead == isRead);
        if (request.Flagged is { } flagged) query = query.Where(mail => mail.Flagged == flagged);
        if (request.HasAttachment is { } hasAttachment) query = query.Where(mail => mail.HasAttachments == hasAttachment);
        if (request.FromDate is { } fromDate) query = query.Where(mail => mail.ReceivedAt >= fromDate);
        if (request.ToDate is { } toDate) query = query.Where(mail => mail.ReceivedAt < toDate);
        if (!string.IsNullOrWhiteSpace(request.From)) query = query.Where(mail => mail.FromAddress == request.From);
        if (!string.IsNullOrWhiteSpace(request.To)) query = query.Where(mail => mail.ToAddress == request.To);
        var useTs = !string.IsNullOrWhiteSpace(queryText) && db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL";
        if (useTs)
        {
            var plain = queryText!;
            // ponytail: participant/attachment fragments stay ILIKE so prefix/fragment search works regardless of tokenization;
            // adopt a dedicated tsvector column over these tables if fragments need ranking.
            query = query.Where(mail => EF.Property<NpgsqlTsVector>(mail, "SearchVector").Matches(EF.Functions.WebSearchToTsQuery("simple", plain))
                || db.Participants.Any(participant => participant.MailId == mail.Id && (EF.Functions.ILike(participant.Address, $"%{plain}%") || EF.Functions.ILike(participant.DisplayName, $"%{plain}%")))
                || db.Attachments.Any(attachment => attachment.MailId == mail.Id && EF.Functions.ILike(attachment.FileName, $"%{plain}%")));
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
        var items = await ordered.Skip((page - 1) * pageSize).Take(pageSize)
            .Select(mail => new MailListItemResponse(mail.Id, mail.MailFolderId, mail.Subject, mail.FromAddress, mail.FromDisplayName, mail.ToAddress, mail.IsRead, mail.HasAttachments, mail.ReceivedAt))
            .ToListAsync(cancellationToken);
        return new(items, page, pageSize, total);
    }
}
