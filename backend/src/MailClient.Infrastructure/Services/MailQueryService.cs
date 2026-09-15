using MailClient.Application.Mail;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Infrastructure.Services;

public sealed class MailQueryService(AppDbContext db)
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 100;
    private const int MaxSearchLength = 100;

    public async Task<MailListResponse> ListAsync(Guid accountId, MailListRequest request, CancellationToken cancellationToken)
    {
        var pageSize = request.PageSize is < 1 ? DefaultPageSize : Math.Min(request.PageSize, MaxPageSize);
        var page = request.Page is < 1 ? 1 : request.Page;
        var search = request.Search?.Trim();
        if (search is { Length: > MaxSearchLength }) search = search[..MaxSearchLength];

        var query = db.Mails.AsNoTracking().Where(mail => mail.MailAccountId == accountId);
        if (request.FolderId is { } folderId)
            query = query.Where(mail => mail.MailFolderId == folderId);
        if (request.IsRead is { } isRead)
            query = query.Where(mail => mail.IsRead == isRead);
        if (request.HasAttachments is { } hasAttachments)
            query = query.Where(mail => mail.HasAttachments == hasAttachments);
        if (!string.IsNullOrEmpty(search))
        {
            if (db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                var pattern = $"%{search}%";
                query = query.Where(mail =>
                    EF.Functions.ILike(mail.Subject, pattern)
                    || EF.Functions.ILike(mail.FromAddress, pattern)
                    || EF.Functions.ILike(mail.FromDisplayName, pattern));
            }
            else
            {
                var normalized = search.ToLowerInvariant();
                query = query.Where(mail =>
                    mail.Subject.ToLower().Contains(normalized)
                    || mail.FromAddress.ToLower().Contains(normalized)
                    || mail.FromDisplayName.ToLower().Contains(normalized));
            }
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(mail => mail.ReceivedAt)
            .ThenByDescending(mail => mail.Uid)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(mail => new MailListItemResponse(
                mail.Id,
                mail.MailFolderId,
                mail.Subject,
                mail.FromAddress,
                mail.FromDisplayName,
                mail.ToAddress,
                mail.IsRead,
                mail.HasAttachments,
                mail.ReceivedAt))
            .ToListAsync(cancellationToken);

        return new MailListResponse(items, page, pageSize, total);
    }
}
