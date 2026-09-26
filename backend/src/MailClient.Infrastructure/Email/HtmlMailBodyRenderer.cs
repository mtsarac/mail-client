
using Ganss.Xss;
using MailClient.Domain.Entities;

namespace MailClient.Infrastructure.Email;

public sealed record MailBodyContract(
    string Html,
    bool HasRemoteContent,
    IReadOnlyList<string> RemoteContentHosts,
    IReadOnlyList<string> RemoteImageHosts,
    IReadOnlyList<string> TrackingPixelHosts,
    bool RemoteImagesAllowed,
    IReadOnlyDictionary<string, Guid> CidAttachmentIds);

public static class HtmlMailBodyRenderer
{
    public static MailBodyContract Render(MailClient.Domain.Entities.Mail mail, string? rawHtml, bool allowRemoteImages = false)
    {
        if (string.IsNullOrWhiteSpace(rawHtml))
            return new MailBodyContract(string.Empty, false, [], [], [], allowRemoteImages, new Dictionary<string, Guid>());

        var html = ResolveCids(mail, rawHtml, out var cidMapping);
        var sanitized = CreateSanitizer().Sanitize(html);
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(sanitized);
        var remoteHosts = CollectRemoteHosts(document.Body);
        var remoteImageHosts = CollectRemoteImageHosts(document.Body);
        NeutralizeRemoteResources(document.Body, allowRemoteImages);

        return new MailBodyContract(
            document.Body?.InnerHtml ?? sanitized,
            remoteHosts.Count > 0,
            remoteHosts.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            remoteImageHosts.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            [],
            allowRemoteImages,
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
        sanitizer.AllowedAttributes.Remove("style");
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

    private static void NeutralizeRemoteResources(AngleSharp.Dom.IElement? root, bool allowRemoteImages)
    {
        if (root is null)
            return;

        foreach (var element in root.QuerySelectorAll("[src], [srcset], [href], [background], [poster], source, track, video, audio, link"))
        {
            foreach (var attribute in new[] { "src", "srcset", "href", "background", "poster" })
            {
                var value = element.GetAttribute(attribute);
                if (string.IsNullOrWhiteSpace(value) || !IsRemoteResource(value, attribute))
                    continue;
                if (allowRemoteImages && element.LocalName == "img" && attribute == "src")
                {
                    if (value.StartsWith("//", StringComparison.Ordinal))
                        element.SetAttribute(attribute, "https:" + value);
                    continue;
                }
                element.SetAttribute($"data-remote-{attribute}", value);
                element.RemoveAttribute(attribute);
            }
        }
    }

    private static bool IsRemoteResource(string value, string attribute) =>
        value.StartsWith("//", StringComparison.Ordinal)
        || (attribute == "srcset"
            ? value.Split(',', StringSplitOptions.RemoveEmptyEntries).Any(candidate => IsRemoteResource(candidate.Trim().Split(' ', 2)[0], "src"))
            : Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https");

    private static List<string> CollectRemoteImageHosts(AngleSharp.Dom.IElement? root) =>
        root is null
            ? []
            : root.QuerySelectorAll("img[src]")
                .Select(element => RemoteHost(element.GetAttribute("src")))
                .Where(host => host is not null)
                .Select(host => host!)
                .ToList();

    private static string? RemoteHost(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return null;
        if (source.StartsWith("//", StringComparison.Ordinal))
            source = "https:" + source;
        return Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https"
            && !string.IsNullOrEmpty(uri.Host)
            ? uri.Host
            : null;
    }

    private static List<string> CollectRemoteHosts(AngleSharp.Dom.IElement? root)
    {
        var hosts = new List<string>();
        if (root is null)
            return hosts;

        foreach (var element in root.QuerySelectorAll("img, [src], [href], [background], video"))
        {
            var source = element.GetAttribute("src")
                ?? element.GetAttribute("href")
                ?? element.GetAttribute("background")
                ?? element.GetAttribute("poster");
            var host = RemoteHost(source);
            if (host is not null)
                hosts.Add(host);
        }

        return hosts;
    }
}
