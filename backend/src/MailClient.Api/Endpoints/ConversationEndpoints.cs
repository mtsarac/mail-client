using MailClient.Application;
using MailClient.Application.Accounts;
using MailClient.Application.Conversations;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Api.Endpoints;

public sealed record ConversationListResponse(IReadOnlyList<ConversationSummaryResponse> Items, int Page, int PageSize, int Total);

public static class ConversationEndpoints
{
    public static void MapConversationEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization().WithTags("Conversations");
        api.MapGet("/conversations", async (int? page, int? pageSize, ICurrentMailAccount current, AppDbContext db, CancellationToken ct) =>
        {
            var size = pageSize is < 1 ? 50 : Math.Min(pageSize ?? 50, 100);
            var number = page is < 1 ? 1 : page ?? 1;
            var accountId = current.MailAccountId;
            var query = db.Conversations
                .AsNoTracking()
                .Where(conversation => conversation.MailAccountId == accountId)
                .OrderByDescending(conversation => conversation.LastMessageAt);
            var total = await query.CountAsync(ct);
            var items = await query
                .Skip((number - 1) * size)
                .Take(size)
                .Select(conversation => new ConversationSummaryResponse(
                    conversation.Id,
                    conversation.NormalizedSubject,
                    db.Mails
                        .Where(mail => mail.ConversationId == conversation.Id)
                        .SelectMany(mail => mail.Participants)
                        .Where(participant => participant.Type == MailClient.Domain.Enums.ParticipantType.From)
                        .Distinct()
                        .OrderBy(participant => participant.SortOrder)
                        .Select(participant => participant.DisplayName != "" ? participant.DisplayName : participant.Address)
                        .Take(10)
                        .ToList(),
                    db.Mails.Count(mail => mail.ConversationId == conversation.Id),
                    db.Mails.Count(mail => mail.ConversationId == conversation.Id && !mail.IsRead),
                    db.Mails.Any(mail => mail.ConversationId == conversation.Id && mail.HasAttachments),
                    conversation.StartedAt,
                    conversation.LastMessageAt))
                .ToListAsync(ct);
            return Results.Ok(new ConversationListResponse(items, number, size, total));
        }).WithName("ListConversations").WithSummary("List account conversations").WithDescription("Newest first. pageSize is capped at 100.").Produces<ConversationListResponse>();

        api.MapGet("/conversations/{id:guid}", async (Guid id, bool? includeTrash, string? include, ICurrentMailAccount current, AppDbContext db, CancellationToken ct) =>
        {
            var accountId = current.MailAccountId;
            var conversation = await db.Conversations
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == id && item.MailAccountId == accountId, ct);
            if (conversation is null)
                return Results.NotFound();
            var self = await db.MailAccounts.Where(a => a.Id == accountId).Select(a => a.NormalizedEmailAddress).SingleAsync(ct);
            var folderTypes = db.MailFolders.Where(folder => folder.MailAccountId == accountId);
            var noTrash = includeTrash == false;
            var messages = await db.Mails
                .AsNoTracking()
                .Where(mail => mail.ConversationId == id && mail.MailAccountId == accountId
                    && !(noTrash && folderTypes.Any(folder => folder.Id == mail.MailFolderId
                        && (folder.FolderType == MailClient.Domain.Enums.MailFolderType.Trash
                            || folder.FolderType == MailClient.Domain.Enums.MailFolderType.Junk))))
                .OrderBy(mail => mail.SentAt)
                .ThenBy(mail => mail.Uid)
                .Select(mail => new ConversationMessageResponse(
                    mail.Id,
                    mail.MailFolderId,
                    mail.Subject,
                    mail.FromAddress,
                    mail.FromDisplayName,
                    mail.SentAt,
                    mail.ReceivedAt,
                    mail.IsRead,
                    mail.HasAttachments,
                    folderTypes.Any(folder => folder.Id == mail.MailFolderId
                        && (folder.FolderType == MailClient.Domain.Enums.MailFolderType.Sent
                            || folder.FolderType == MailClient.Domain.Enums.MailFolderType.Drafts))
                        || mail.FromAddress.ToLower() == self))
                .ToListAsync(ct);
            if (include == "body")
            {
                var ids = messages.Select(m => m.Id).ToList();
                var entities = await db.Mails.AsNoTracking().Include(m => m.Attachments)
                    .Where(m => ids.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);
                messages = messages.Select(m =>
                {
                    var mail = entities[m.Id];
                    var body = MailClient.Infrastructure.Email.HtmlMailBodyRenderer.Render(mail, mail.BodyHtml);
                    return m with
                    {
                        BodyText = mail.BodyText,
                        Body = new MailClient.Application.Mail.MailBodyResponse(body.Html, body.HasRemoteContent, body.RemoteContentHosts, body.TrackingPixelHosts)
                    };
                }).ToList();
            }
            return Results.Ok(new ConversationDetailResponse(conversation.Id, conversation.NormalizedSubject, messages));
        }).WithName("GetConversation").WithSummary("Get conversation with messages").WithDescription("Messages from all folders. includeTrash=false omits Trash and Junk. include=body adds bodyText and the sanitized body to every message (one request instead of N). isFromMe is true for Sent/Drafts mails or the account address.").Produces<ConversationDetailResponse>().Produces(404);
    }
}
