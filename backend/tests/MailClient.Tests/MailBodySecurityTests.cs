using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Email;

namespace MailClient.Tests;

public sealed class MailBodySecurityTests
{
    private static readonly Guid MailId = Guid.NewGuid();

    private static MailClient.Domain.Entities.Mail CreateMail(params Attachment[] attachments)
    {
        var mail = new MailClient.Domain.Entities.Mail
        {
            Id = MailId,
            MailAccountId = Guid.NewGuid(),
            MailFolderId = Guid.NewGuid(),
            Subject = "security"
        };
        foreach (var attachment in attachments)
        {
            attachment.MailId = MailId;
            mail.Attachments.Add(attachment);
        }
        return mail;
    }

    [Fact]
    public void Sanitize_RemovesScriptIframeAndEventHandlers()
    {
        const string raw = """
            <p>ok</p><script>alert(1)</script><iframe src="https://evil.test"></iframe><object data="x"></object><embed src="x"><div onclick="alert(1)">tıkla</div><form action="steal"><input/></form>
            """;
        var mail = CreateMail();

        var contract = HtmlMailBodyRenderer.Render(mail, raw);

        Assert.Contains("ok", contract.Html);
        Assert.DoesNotContain("<script", contract.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", contract.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<object", contract.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<embed", contract.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", contract.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<form", contract.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("vbscript:msg")]
    public void Sanitize_RemovesUnsafeUrlSchemes(string href)
    {
        var mail = CreateMail();

        var contract = HtmlMailBodyRenderer.Render(mail, $"""<a href="{href}">tıkla</a>""");

        Assert.DoesNotContain("javascript:", contract.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("file:", contract.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vbscript:", contract.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sanitize_DetectsRemoteImages()
    {
        var mail = CreateMail();

        var contract = HtmlMailBodyRenderer.Render(mail, """<p>merhaba</p><img src="https://tracker.example/pixel.png">""");

        Assert.True(contract.HasRemoteContent);
        Assert.Contains("tracker.example", contract.RemoteContentHosts);
        Assert.DoesNotContain("<img src=\"https://", contract.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("data-remote-src", contract.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sanitize_DetectsProtocolRelativeRemoteImages()
    {
        var mail = CreateMail();

        var contract = HtmlMailBodyRenderer.Render(mail, """<img src="//tracker.example/pixel.png">""");

        Assert.True(contract.HasRemoteContent);
        Assert.Contains("tracker.example", contract.RemoteContentHosts);
    }

    [Fact]
    public void Sanitize_NeutralizesProtocolRelativeSrcAndHref()
    {
        var mail = CreateMail();

        var contract = HtmlMailBodyRenderer.Render(mail, """<a href="//bad.example/landing">yönlendir</a>""");

        Assert.Contains("data-remote-href=\"//bad.example/landing\"", contract.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" href=\"//", contract.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sanitize_StripsStyleAttribute_RemovingCssUrlVector()
    {
        var mail = CreateMail();

        var contract = HtmlMailBodyRenderer.Render(mail, """<div style="background:url(https://track.test/px.png)">x</div>""");

        Assert.DoesNotContain("style=", contract.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("url(", contract.Html, StringComparison.OrdinalIgnoreCase);
        Assert.False(contract.HasRemoteContent);
    }

    [Fact]
    public void Sanitize_PlainMailWithoutRemoteContent_HasNoRemoteContent()
    {
        var mail = CreateMail();

        var contract = HtmlMailBodyRenderer.Render(mail, "<p>merhaba</p>");

        Assert.False(contract.HasRemoteContent);
    }

    [Fact]
    public void Sanitize_ResolvesCidToAttachmentResource()
    {
        var attachment = new Attachment
        {
            Id = Guid.NewGuid(),
            ContentId = "<logo123>",
            FileName = "logo.png",
            ContentType = "image/png",
            IsInline = true
        };
        var mail = CreateMail(attachment);

        var contract = HtmlMailBodyRenderer.Render(mail, """<img src="cid:LOGO123">""");

        Assert.Contains($"/api/mails/{MailId}/attachments/{attachment.Id}", contract.Html);
        Assert.DoesNotContain("cid:", contract.Html, StringComparison.OrdinalIgnoreCase);
        Assert.False(contract.HasRemoteContent);
    }
}
