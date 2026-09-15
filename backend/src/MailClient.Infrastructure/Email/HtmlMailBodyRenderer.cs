
using Ganss.Xss;
using MailClient.Domain.Entities;

namespace MailClient.Infrastructure.Email;

public sealed record MailBodyContract(
    string Html,
    bool HasRemoteContent,
    IReadOnlyList<string> RemoteContentHosts,
    IReadOnlyList<string> TrackingPixelHosts,
    IReadOnlyDictionary<string, Guid> CidAttachmentIds);

public static class HtmlMailBodyRenderer
{
    private static readonly string[] TrackingSizeHeuristicsUrlSuffixes = { "pixel", "track", "beacon", "open", "click" };

    public static MailBodyContract Render(MailClient.Domain.Entities.Mail mail, string? rawHtml)
    {
        if (string.IsNullOrWhiteSpace(rawHtml))
            return new MailBodyContract(string.Empty, false, [], [], new Dictionary<string, Guid>());

        var html = ResolveCids(mail, rawHtml, out var cidMapping);
        var sanitizer = CreateSanitizer();
        var sanitized = sanitizer.Sanitize(html);

        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(sanitized);
        var remoteHosts = CollectRemoteHosts(document.Body);
        var trackingHosts = remoteHosts
            .Where(host => TrackingSizeHeuristicsUrlSuffixes.Any(host.Contains))
            .ToList();

        return new MailBodyContract(
            sanitized,
            remoteHosts.Count > 0,
            remoteHosts.Distinct().ToList(),
            trackingHosts,
            cidMapping);
    }

    private static HtmlSanitizer CreateSanitizer()
    {
        var sanitizer = new HtmlSanitizer();
        foreach (var scheme in new[] { "javascript", "data", "file", "vbscript" })
            sanitizer.AllowedSchemes.Remove(scheme);
        sanitizer.AllowedTags.Remove("form");
        sanitizer.AllowedTags.Remove("input");
        sanitizer.AllowedTags.Remove("button");
        return sanitizer;
    }

    private static string ResolveCids(MailClient.Domain.Entities.Mail mail, string html, out Dictionary<string, Guid> mapping)
    {
        mapping = mail.Attachments
            .Where(attachment => !string.IsNullOrEmpty(attachment.ContentId))
            .ToDictionary(
                attachment => attachment.ContentId.Trim('<', '>'),
                attachment => attachment.Id,
                StringComparer.OrdinalIgnoreCase);

        var resolved = html;
        foreach (var (contentId, attachmentId) in mapping)
        {
            var target = $"/api/mails/{mail.Id}/attachments/{attachmentId}";
            resolved = resolved.Replace($"cid:{contentId}", target, StringComparison.OrdinalIgnoreCase);
        }

        return resolved;
    }

    private static List<string> CollectRemoteHosts(AngleSharp.Dom.IElement? root)
    {
        var hosts = new List<string>();
        if (root is null)
            return hosts;

        foreach (var element in root.QuerySelectorAll("img, [src], [href], [background]"))
        {
            var source = element.GetAttribute("src")
                ?? element.GetAttribute("href")
                ?? element.GetAttribute("background");
            if (string.IsNullOrEmpty(source)
                || source.StartsWith("cid:", StringComparison.OrdinalIgnoreCase)
                || source.StartsWith('#')
                || source.StartsWith('/'))
                continue;

            if (Uri.TryCreate(source, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https"
                && !string.IsNullOrEmpty(uri.Host))
            {
                hosts.Add(uri.Host);
            }
        }

        return hosts;
    }
}
