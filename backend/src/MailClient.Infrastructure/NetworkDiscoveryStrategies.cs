using System.Net.Http.Json;
using System.Xml;
using System.Xml.Linq;
using DnsClient;
using DnsClient.Protocol;
using MailClient.Application.Discovery;
using MailClient.Domain.Enums;
using Microsoft.Extensions.Options;

namespace MailClient.Infrastructure.Discovery;

public sealed class DocumentLimits
{
    public static readonly XmlReaderSettings Xml = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = 256 * 1024,
        Async = true
    };

    public static async Task<byte[]> ReadBoundedAsync(Stream source, int maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var output = new MemoryStream();
        try
        {
            while (await source.ReadAsync(buffer, cancellationToken) is { } read and > 0)
            {
                if (output.Length + read > maxBytes)
                    throw new InvalidOperationException("discovery_document_too_large");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        catch (InvalidOperationException ex) when (ex.Message == "discovery_document_too_large")
        {
            throw;
        }

        return output.ToArray();
    }
}

public class DnsSrvDiscoveryStrategy(LookupClient dns) : IMailDiscoveryStrategy
{
    public int Order => 2;

    public async IAsyncEnumerable<MailServerCandidate> DiscoverAsync(
        string email,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var domain = email[(email.LastIndexOf('@') + 1)..];
        var imap = await QueryAsync(domain, ["_imaps._tcp", "_imap._tcp"], cancellationToken);
        var smtp = await QueryAsync(domain, ["_submissions._tcp", "_submission._tcp"], cancellationToken);
        if (imap is not null && smtp is not null)
        {
            yield return new(
                MailProvider.Custom,
                new(imap.Value.Host, imap.Value.Port, imap.Value.Port == 993 ? MailSecurity.SslOnConnect : MailSecurity.StartTls),
                new(smtp.Value.Host, smtp.Value.Port, smtp.Value.Port == 465 ? MailSecurity.SslOnConnect : MailSecurity.StartTls),
                [AuthenticationMethod.Password, AuthenticationMethod.AppSpecificPassword],
                DiscoverySource.DnsSrv);
        }
    }

    protected virtual async Task<(string Host, int Port)?> QueryAsync(string domain, string[] services, CancellationToken cancellationToken)
    {
        foreach (var service in services)
        {
            var response = await dns.QueryAsync($"{service}.{domain}", QueryType.SRV, cancellationToken: cancellationToken);
            var record = response.Answers.SrvRecords().OrderBy(x => x.Priority).ThenByDescending(x => x.Weight).FirstOrDefault();
            if (record is not null) return (record.Target.Value.TrimEnd('.'), record.Port);
        }
        return null;
    }
}

public sealed class AutoconfigDiscoveryStrategy(HttpClient http, IOptions<MailDiscoveryOptions> options) : IMailDiscoveryStrategy
{
    public int Order => 3;

    public async IAsyncEnumerable<MailServerCandidate> DiscoverAsync(
        string email,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var domain = email[(email.LastIndexOf('@') + 1)..];
        var urls = new[]
        {
            $"https://autoconfig.{domain}/mail/config-v1.1.xml?emailaddress={Uri.EscapeDataString(email)}",
            $"https://{domain}/.well-known/autoconfig/mail/config-v1.1.xml?emailaddress={Uri.EscapeDataString(email)}"
        };
        foreach (var url in urls)
        {
            XDocument document;
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                if (!response.IsSuccessStatusCode) continue;
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var bytes = await DocumentLimits.ReadBoundedAsync(stream, options.Value.MaxDocumentBytes, cancellationToken);
                using var reader = XmlReader.Create(new MemoryStream(bytes), DocumentLimits.Xml);
                document = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken);
            }
            var incoming = document.Descendants("incomingServer").FirstOrDefault(x => (string?)x.Attribute("type") == "imap");
            var outgoing = document.Descendants("outgoingServer").FirstOrDefault(x => (string?)x.Attribute("type") == "smtp");
            if (Parse(incoming) is { } imap && Parse(outgoing) is { } smtp)
                yield return new(MailProvider.Custom, imap, smtp, [AuthenticationMethod.Password, AuthenticationMethod.AppSpecificPassword], DiscoverySource.Autoconfig);
        }
    }

    private static MailEndpoint? Parse(XElement? element)
    {
        if (element is null || !int.TryParse(element.Element("port")?.Value, out var port)) return null;
        var host = element.Element("hostname")?.Value.Trim();
        var socket = element.Element("socketType")?.Value;
        if (string.IsNullOrWhiteSpace(host)) return null;
        return socket?.ToUpperInvariant() switch
        {
            "SSL" => new(host, port, MailSecurity.SslOnConnect),
            "STARTTLS" => new(host, port, MailSecurity.StartTls),
            _ => null
        };
    }
}

public sealed class MicrosoftAutodiscoverStrategy(HttpClient http, IOptions<MailDiscoveryOptions> options) : IMailDiscoveryStrategy
{
    public int Order => 4;

    public async IAsyncEnumerable<MailServerCandidate> DiscoverAsync(
        string email,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var domain = email[(email.LastIndexOf('@') + 1)..];
        var url = $"https://autodiscover.{domain}/autodiscover/autodiscover.json?Email={Uri.EscapeDataString(email)}&Protocol=IMAP";
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) yield break;
        AutodiscoverResponse? payload;
        await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
        {
            var bytes = await DocumentLimits.ReadBoundedAsync(stream, options.Value.MaxDocumentBytes, cancellationToken);
            payload = System.Text.Json.JsonSerializer.Deserialize<AutodiscoverResponse>(bytes);
        }
        if (payload?.Url is null || !Uri.TryCreate(payload.Url, UriKind.Absolute, out var discovered) || discovered.Scheme != Uri.UriSchemeHttps) yield break;
        yield return new(MailProvider.Microsoft, new(discovered.Host, 993, MailSecurity.SslOnConnect), new(discovered.Host, 587, MailSecurity.StartTls), [AuthenticationMethod.Password, AuthenticationMethod.AppSpecificPassword], DiscoverySource.Autodiscover);
    }

    private sealed record AutodiscoverResponse(string? Protocol, string? Url);
}
