using System.Net.Sockets;

namespace MailClient.Infrastructure.Network;

public sealed class SsrfSafeDiscoveryHttpHandler : HttpMessageHandler
{
    private readonly SocketsHttpHandler _inner;
    private readonly HttpMessageInvoker _invoker;
    private readonly OutboundHostValidator _validator;

    public SsrfSafeDiscoveryHttpHandler(OutboundHostValidator validator)
    {
        _validator = validator;
        _inner = new SocketsHttpHandler { AllowAutoRedirect = false, ConnectCallback = ConnectAsync };
        _invoker = new HttpMessageInvoker(_inner, disposeHandler: false);
    }

    public bool AllowAutoRedirect => _inner.AllowAutoRedirect;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        _invoker.SendAsync(request, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var validated = await _validator.ResolveAllowedAsync(context.DnsEndPoint.Host, cancellationToken);
        var socket = new Socket(validated.Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(validated.Address, context.DnsEndPoint.Port, cancellationToken);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        return new NetworkStream(socket, ownsSocket: true);
    }
}
