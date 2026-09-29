using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using MailClient.Application.Discovery;
using MailClient.Application.Mail;
using MailClient.Application.Runtime;
using MailClient.Infrastructure.Mail;
using Microsoft.Extensions.Caching.Memory;

namespace MailClient.Infrastructure.Accounts;

/// <summary>Coalesces identical credential checks and backs off rejected secrets without blocking corrections.</summary>
public sealed class AuthenticationAttemptCache(TimeProvider? clock = null) : IDisposable
{
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly MemoryCache _rejections = new(new MemoryCacheOptions { SizeLimit = 4096 });
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Exception?>> _inFlight = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task ValidateAsync(IMailConnectionValidator validator, MailServerCandidate candidate,
        string accountEmail, string username, string password, RuntimeAuthenticationSettings settings, CancellationToken cancellationToken)
    {
        var identity = $"{candidate.Imap.Host.Trim().ToLowerInvariant()}:{candidate.Imap.Port}|{candidate.Smtp.Host.Trim().ToLowerInvariant()}:{candidate.Smtp.Port}|{accountEmail.Trim().ToUpperInvariant()}|{username.Trim().ToUpperInvariant()}|{Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(password)))}";
        cancellationToken.ThrowIfCancellationRequested();
        if (Blocked(identity) is { } rejection)
            throw rejection;

        var pending = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var flight = _inFlight.GetOrAdd(identity, pending);
        if (!ReferenceEquals(flight, pending))
        {
            var result = await flight.Task.WaitAsync(cancellationToken);
            if (result is not null)
                throw result;
            return;
        }

        try
        {
            // A previous attempt may have finished after the initial cache check, before this flight began.
            if (Blocked(identity) is { } recent)
            {
                pending.SetResult(recent);
                throw recent;
            }
            try
            {
                await validator.ValidateCredentialsAsync(candidate, username, password, cancellationToken);
                _rejections.Remove(identity);
                pending.SetResult(null);
            }
            catch (MailConnectionException ex) when (ex.Failure == MailConnectionFailure.Authentication)
            {
                var failures = _rejections.TryGetValue(identity, out Rejection? previous) ? Math.Min(previous!.Failures + 1, 31) : 1;
                var delay = Math.Min((long)settings.RetryBaseDelaySeconds << Math.Min(failures - 1, 30), settings.RetryMaxDelaySeconds);
                _rejections.Set(identity, new Rejection(failures, _clock.GetUtcNow().AddSeconds(delay), ex.Operation),
                    new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds((long)settings.RetryMaxDelaySeconds * 2), Size = 1 });
                pending.SetResult(ex);
                throw;
            }
        }
        catch (Exception ex)
        {
            pending.TrySetResult(ex);
            throw;
        }
        finally
        {
            _inFlight.TryRemove(identity, out _);
        }
    }

    private MailConnectionException? Blocked(string identity) =>
        _rejections.TryGetValue(identity, out Rejection? rejection) && _clock.GetUtcNow() < rejection!.NextAttemptAt
            ? new MailConnectionException(MailConnectionFailure.Authentication, "Mail authentication failed.") { Operation = rejection.Operation }
            : null;

    private sealed record Rejection(int Failures, DateTimeOffset NextAttemptAt, string Operation);

    public void Dispose() => _rejections.Dispose();
}
