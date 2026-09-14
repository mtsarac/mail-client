using System.Net;
using System.Net.Sockets;
using MailClient.Application.Discovery;
using MailClient.Application.Mail;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Mail;
using MailClient.Infrastructure.Network;
using Microsoft.Extensions.Logging.Abstractions;

namespace MailClient.Tests;

public sealed class MailConnectionHelperTests
{
    [Fact]
    public async Task WithImapAsync_ConnectsToValidatedAddress_AndNeverLeaksRawErrors()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = new TaskCompletionSource();
        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            accepted.TrySetResult();
            client.Close();
        });

        var helper = new MailConnectionHelper(new LoopbackHostValidator(), NullLogger<MailConnectionHelper>.Instance);
        var endpoint = new MailServerEndpoint("mail.example.test", port, MailSecurity.SslOnConnect);

        var failure = await Assert.ThrowsAsync<MailConnectionException>(() =>
            helper.WithImapAsync(endpoint, "user@example.test", "p@ssw0rd", "ValidateImap", static (_, _) => Task.FromResult(true), CancellationToken.None));

        await accepted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("ValidateImap", failure.Operation);
        Assert.DoesNotContain("mail.example.test", failure.Message);
        Assert.DoesNotContain("127.0.0.1", failure.Message);
        Assert.DoesNotContain("p@ssw0rd", failure.Message);
    }

    [Fact]
    public async Task WithImapAsync_RealValidator_BlocksLoopbackBeforeAnyConnection()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var helper = new MailConnectionHelper(
            new OutboundHostValidator(new FakeDns(IPAddress.Loopback)),
            NullLogger<MailConnectionHelper>.Instance);
        var endpoint = new MailServerEndpoint("127.0.0.1", port, MailSecurity.SslOnConnect);

        await Assert.ThrowsAsync<MailConnectionException>(() =>
            helper.WithImapAsync(endpoint, "user", "secret", "ValidateImap", static (_, _) => Task.FromResult(true), CancellationToken.None));

        Assert.False(listener.Pending());
    }

    [Fact]
    public async Task ConnectionValidator_RejectsUnsupportedPortsSecurityModesAndHosts()
    {
        var validator = new MailKitConnectionValidator(
            new OutboundHostValidator(new FakeDns(IPAddress.Parse("93.184.216.34"))),
            new MailConnectionHelper(new LoopbackHostValidator(), NullLogger<MailConnectionHelper>.Instance));

        Assert.False(await validator.ValidateCandidateAsync(Candidate("imap.example.test", 2525, MailSecurity.SslOnConnect), CancellationToken.None));
        Assert.False(await validator.ValidateCandidateAsync(Candidate("imap.example.test", 993, MailSecurity.StartTls), CancellationToken.None));
        Assert.False(await validator.ValidateCandidateAsync(Candidate("127.0.0.1", 993, MailSecurity.SslOnConnect), CancellationToken.None));
        Assert.False(await validator.ValidateCandidateAsync(Candidate("imap.example.test", 993, MailSecurity.SslOnConnect), CancellationToken.None));
    }

    [Fact]
    public async Task ConnectionValidator_UnsafeCandidate_ThrowsUnsafeCode()
    {
        var validator = new MailKitConnectionValidator(
            new OutboundHostValidator(new FakeDns(IPAddress.Parse("93.184.216.34"))),
            new MailConnectionHelper(new LoopbackHostValidator(), NullLogger<MailConnectionHelper>.Instance));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            validator.ValidateCredentialsAsync(Candidate("localhost", 993, MailSecurity.SslOnConnect), "user", "secret", CancellationToken.None));

        Assert.Equal("mail_server_unsafe", error.Message);
    }

    private static MailServerCandidate Candidate(string imapHost, int imapPort, MailSecurity imapSecurity) => new(
        MailProvider.Custom,
        new MailEndpoint(imapHost, imapPort, imapSecurity),
        new MailEndpoint("smtp.example.test", 465, MailSecurity.SslOnConnect),
        [AuthenticationMethod.Password],
        DiscoverySource.Manual);

    private sealed class LoopbackHostValidator() : OutboundHostValidator(new FakeDns(IPAddress.Loopback))
    {
        public override Task<HostValidationResult> ValidateAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult(new HostValidationResult(true));

        public override Task<ValidatedHost> ResolveAllowedAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult(new ValidatedHost(host, IPAddress.Loopback));
    }
}
