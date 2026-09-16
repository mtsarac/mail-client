using MailClient.Application.Mail;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Infrastructure.Services;

public sealed class MailQueryService(MailSearchService search)
{
    public Task<MailListResponse> ListAsync(Guid accountId, MailListRequest request, CancellationToken cancellationToken) =>
        search.SearchAsync(
            accountId,
            new MailSearchRequest(request.Search, request.FolderId, null, null, null, null, null, request.IsRead, null, request.HasAttachments, request.Page, request.PageSize),
            cancellationToken);
}
