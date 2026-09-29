using System.Net;
using System.Net.Sockets;
using System.Text;
using MailClient.Application.Sync;
using MailClient.Infrastructure.Mail;
using MailClient.Infrastructure.Network;
using MailClient.Infrastructure.Sync;
using Microsoft.Extensions.DependencyInjection;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace MailClient.Tests;

public sealed class ImapIdleWatcherTests
{
    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(20, 300)]
    public void Backoff_DoublesFromFiveSecondsAndCapsAtFiveMinutes(int failures, int expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), ImapIdleWatcher.Backoff(failures));

    [Fact]
    public async Task DisabledWatcher_ReturnsImmediatelyWithoutTouchingTheDatabase()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var watcher = new ImapIdleWatcher(
            services.GetRequiredService<IServiceScopeFactory>(),
            new MailConnectionHelper(new OutboundHostValidator(new SystemDnsResolver()), NullLogger<MailConnectionHelper>.Instance),
            new NoopScheduler(),
            new ImapIdleOptions { Enabled = false },
            NullLogger<ImapIdleWatcher>.Instance);

        await watcher.StartAsync(CancellationToken.None);
        await watcher.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        await watcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Idle_SyncsOnConnectAndAgainWhenTheServerReportsANewMessage()
    {

        await using var server = new FakeImapServer();
        using var client = new ImapClient();
        await client.ConnectAsync(IPAddress.Loopback.ToString(), server.Port, SecureSocketOptions.None).WaitAsync(TimeSpan.FromSeconds(10));
        await client.AuthenticateAsync("user", "pass").WaitAsync(TimeSpan.FromSeconds(10));

        var calls = 0;
        var second = new TaskCompletionSource();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var idle = ImapIdleWatcher.IdleAsync(client, _ =>
        {
            if (Interlocked.Increment(ref calls) == 2)
                second.TrySetResult();
            return Task.CompletedTask;
        }, TimeSpan.FromMinutes(9), stop.Token);

        try { await server.IdlingAsync().WaitAsync(TimeSpan.FromSeconds(10)); } catch (TimeoutException) { throw new Exception("server saw: " + server.Log + " idle: " + (idle.IsFaulted ? idle.Exception!.ToString() : idle.Status.ToString())); }

        Assert.Equal(1, Volatile.Read(ref calls));
        await server.SendAsync("* 2 EXISTS");
        await second.Task.WaitAsync(TimeSpan.FromSeconds(10));

        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => idle);
    }

    /// <summary>Just enough IMAP to log in, select INBOX and IDLE.</summary>
    private sealed class FakeImapServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly TaskCompletionSource _idling = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _loop;
        private StreamWriter? _writer;
        private readonly StringBuilder _log = new();
        public string Log => _log.ToString();

        public FakeImapServer()
        {
            _listener.Start();
            _loop = RunAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public Task IdlingAsync() => _idling.Task;

        public async Task SendAsync(string line)
        {
            await _writer!.WriteAsync(line + "\r\n");
            await _writer.FlushAsync();
        }

        private async Task RunAsync()
        {
            try
            {
                using var tcp = await _listener.AcceptTcpClientAsync();
                await using var stream = tcp.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                _writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };
                await SendAsync("* OK [CAPABILITY IMAP4rev1 IDLE] ready");
                string? line;
                while ((line = await reader.ReadLineAsync()) is not null)
                {
                    _log.Append(line).Append(" | ");
                    var parts = line.Split(' ', 3);
                    var tag = parts[0];
                    var command = parts.Length > 1 ? parts[1].ToUpperInvariant() : "";
                    switch (command)
                    {
                        case "CAPABILITY":
                            await SendAsync("* CAPABILITY IMAP4rev1 IDLE");
                            await SendAsync($"{tag} OK done");
                            break;
                        case "LOGIN":
                            await SendAsync($"{tag} OK [CAPABILITY IMAP4rev1 IDLE] logged in");
                            break;
                        case "NAMESPACE":
                            await SendAsync("* NAMESPACE ((\"\" \"/\")) NIL NIL");
                            await SendAsync($"{tag} OK done");
                            break;
                        case "LIST":
                            await SendAsync("* LIST (\\HasNoChildren) \"/\" \"INBOX\"");
                            await SendAsync($"{tag} OK done");
                            break;
                        case "SELECT":
                        case "EXAMINE":
                            await SendAsync("* 1 EXISTS");
                            await SendAsync("* 0 RECENT");
                            await SendAsync("* FLAGS (\\Seen)");
                            await SendAsync("* OK [UIDVALIDITY 1] ok");
                            await SendAsync("* OK [UIDNEXT 2] ok");
                            await SendAsync($"{tag} OK [READ-ONLY] done");
                            break;
                        case "IDLE":
                            await SendAsync("+ idling");
                            _idling.TrySetResult();
                            while ((line = await reader.ReadLineAsync()) is not null && !line.Equals("DONE", StringComparison.OrdinalIgnoreCase)) { }
                            await SendAsync($"{tag} OK idle terminated");
                            break;
                        case "LOGOUT":
                            await SendAsync("* BYE");
                            await SendAsync($"{tag} OK bye");
                            return;
                        default:
                            await SendAsync($"{tag} OK done");
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            await _loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
    }

    private sealed class NoopScheduler : ISyncScheduler
    {
        public ValueTask ScheduleAccountAsync(Guid accountId, SyncOrigin origin, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask ScheduleFolderAsync(Guid accountId, Guid folderId, SyncOrigin origin, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<Guid> ScheduleUserFolderJobAsync(Guid accountId, Guid folderId, CancellationToken cancellationToken) => ValueTask.FromResult(Guid.Empty);
        public SyncJobStatus? GetJobStatus(Guid accountId, Guid jobId) => null;
    }
}
