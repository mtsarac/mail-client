using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MailClient.Infrastructure.Push;

// FCM HTTP v1 transport over BCL HttpClient. Tokens chunk at the documented
// FCM multicast limit so a future SDK transport maps one chunk to one call.
public sealed class FcmHttpGateway : IFirebaseGateway, IDisposable
{
    public const int MaxTokensPerBatch = 500;
    private const int MaxConcurrentSends = 10;

    private readonly FirebaseOptions _options;
    private readonly IFirebaseAccessTokenProvider _tokens;
    private readonly ILogger<FcmHttpGateway> _logger;
    private readonly HttpClient _http;
    private bool _disposed;

    public FcmHttpGateway(
        FirebaseOptions options,
        ILogger<FcmHttpGateway> logger,
        IFirebaseAccessTokenProvider tokens)
        : this(options, logger, tokens, FirebaseHttp.Create())
    {
    }

    internal FcmHttpGateway(
        FirebaseOptions options,
        ILogger<FcmHttpGateway> logger,
        IFirebaseAccessTokenProvider tokens,
        HttpClient http)
    {
        _options = options;
        _logger = logger;
        _tokens = tokens;
        _http = http;
    }

    public async Task<IReadOnlyList<FirebaseSendResult>> SendNewMailAsync(
        IReadOnlyList<FirebaseRecipient> recipients,
        string title,
        string body,
        IReadOnlyDictionary<string, string> data,
        CancellationToken cancellationToken)
    {
        var results = new List<FirebaseSendResult>(recipients.Count);
        foreach (var chunk in recipients.Chunk(MaxTokensPerBatch))
        {
            using var gate = new SemaphoreSlim(MaxConcurrentSends, MaxConcurrentSends);
            var tasks = chunk.Select(recipient => SendOneAsync(recipient, title, body, data, gate, cancellationToken)).ToList();
            results.AddRange(await Task.WhenAll(tasks));
        }

        return results;
    }

    private async Task<FirebaseSendResult> SendOneAsync(
        FirebaseRecipient recipient,
        string title,
        string body,
        IReadOnlyDictionary<string, string> data,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var token = await _tokens.GetAccessTokenAsync(cancellationToken);
            var url = $"https://fcm.googleapis.com/v1/projects/{Uri.EscapeDataString(_options.ProjectId)}/messages:send";
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    message = new
                    {
                        token = recipient.PushToken,
                        notification = new { title, body },
                        data
                    }
                }),
                Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
                return new FirebaseSendResult(recipient.DbId, true, false);

            var status = await ReadErrorStatusAsync(response, cancellationToken);
            // Only a definitive unregistered response removes the token;
            // quota, auth, transient and payload failures keep it.
            var remove = string.Equals(status, "NOT_FOUND", StringComparison.OrdinalIgnoreCase);
            _logger.LogWarning(
                "FCM send failed for device {DeviceId}: HTTP {StatusCode}, provider status {ProviderStatus}.",
                recipient.DbId, (int)response.StatusCode, status ?? "unknown");
            return new FirebaseSendResult(recipient.DbId, false, remove);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FCM send failed for device {DeviceId}.", recipient.DbId);
            return new FirebaseSendResult(recipient.DbId, false, false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<string?> ReadErrorStatusAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("status", out var status))
                return status.GetString();
        }
        catch (Exception)
        {
            // Unparseable error bodies stay transient: never remove the token.
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _http.Dispose();
    }
}
