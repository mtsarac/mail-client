using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace MailClient.Api.OpenApi;

/// <summary>Declares tags in a fixed, workflow-friendly order with one-line descriptions.</summary>
public sealed class TagOrderDocumentFilter : IDocumentFilter
{
    private static readonly (string Name, string Description)[] Tags =
    [
        ("Accounts", "Anonymous. Discover/connect a new mailbox or log in to an existing one."),
        ("OAuth", "Anonymous. Google/Microsoft authorization-code flow (start, then complete)."),
        ("Authentication", "Anonymous. Refresh (rotating) and revoke sessions with a refresh token."),
        ("Account", "Bearer. Current mailbox, reconnect, sessions and deletion."),
        ("Folders", "Bearer. Folder list, refresh from server, and asynchronous sync."),
        ("Mail", "Bearer. List, read and download mail."),
        ("Search", "Bearer. Filtered mail search."),
        ("Mail Operations", "Bearer. Read/star/trash/archive/spam/move/copy and bulk operations (remote-first)."),
        ("Compose", "Bearer. Reply/reply-all/forward context and sending."),
        ("Drafts", "Bearer. Server-side drafts."),
        ("Conversations", "Bearer. Threaded conversations."),
        ("Devices", "Bearer. Push notification device registration."),
        ("Management", "X-Management-Key. Runtime settings and email allowlist.")
    ];

    public void Apply(OpenApiDocument document, DocumentFilterContext context)
    {
        // Orphaned by the hand-written multipart bodies (Swashbuckle models IFormCollection as key/value pairs).
        document.Components?.Schemas?.Remove("StringStringValuesKeyValuePair");
        document.Tags = new HashSet<OpenApiTag>(Tags.Select(tag => new OpenApiTag { Name = tag.Name, Description = tag.Description }));
    }
}
