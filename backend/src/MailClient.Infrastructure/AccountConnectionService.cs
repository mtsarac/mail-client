using MailClient.Application.Accounts;
using MailClient.Application.Discovery;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Authentication;
using MailClient.Infrastructure.Discovery;
using MailClient.Infrastructure.Mail;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Infrastructure.Accounts;

public sealed class AccountConnectionService(AppDbContext db, IMailConnectionValidator connections, ICredentialProtector protector, MailSessionService sessions, IJwtTokenIssuer jwt, InitialSyncQueue syncQueue)
{
    public async Task<TokenResponse> ConnectAsync(DiscoveryState state, AuthenticationInput authentication, string? deviceIdentifier, CancellationToken cancellationToken) =>
        await ConnectCoreAsync(state.Email, state.Email, null, state.Candidate, authentication, deviceIdentifier, cancellationToken);

    public async Task<TokenResponse> ConnectManualAsync(ManualConnectRequest request, CancellationToken cancellationToken)
    {
        var candidate = new MailServerCandidate(MailProvider.Custom, new(request.Imap.Host, request.Imap.Port, request.Imap.Security), new(request.Smtp.Host, request.Smtp.Port, request.Smtp.Security), [AuthenticationMethod.Password, AuthenticationMethod.AppSpecificPassword], DiscoverySource.Manual);
        return await ConnectCoreAsync(request.Email, request.Username, request.DisplayName, candidate, request.Authentication, request.DeviceIdentifier, cancellationToken);
    }

    private async Task<TokenResponse> ConnectCoreAsync(string email, string username, string? displayName, MailServerCandidate candidate, AuthenticationInput authentication, string? deviceIdentifier, CancellationToken cancellationToken)
    {
        if (authentication.Type is not (AuthenticationMethod.Password or AuthenticationMethod.AppSpecificPassword) || string.IsNullOrWhiteSpace(authentication.Password)) throw new InvalidOperationException("unsupported_authentication_method");
        if (!email.Contains('@', StringComparison.Ordinal)) throw new InvalidOperationException("invalid_email");
        var local = email[..email.IndexOf('@')];
        var usernames = new[] { username, email, local }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase);
        string? working = null;
        foreach (var candidateUsername in usernames)
        {
            try
            {
                await connections.ValidateCredentialsAsync(candidate, candidateUsername, authentication.Password, cancellationToken);
                working = candidateUsername;
                break;
            }
            catch (MailKit.Security.AuthenticationException) when (candidateUsername != usernames.Last())
            {
                continue;
            }
        }
        if (working is null) throw new InvalidOperationException("mail_authentication_failed");
        var normalized = email.Trim().ToUpperInvariant();
        var account = await db.MailAccounts.Include(x => x.Credentials).SingleOrDefaultAsync(x => x.NormalizedEmailAddress == normalized, cancellationToken);
        var now = DateTime.UtcNow;
        if (account is null)
        {
            account = new MailAccount { Id = Guid.NewGuid(), EmailAddress = email.Trim(), NormalizedEmailAddress = normalized, CreatedAt = now };
            db.MailAccounts.Add(account);
        }
        account.DisplayName = displayName?.Trim() ?? account.DisplayName;
        account.Username = working;
        account.Provider = candidate.Provider;
        account.AuthenticationMethod = authentication.Type;
        account.ImapHost = candidate.Imap.Host; account.ImapPort = candidate.Imap.Port; account.ImapSecurity = candidate.Imap.Security;
        account.SmtpHost = candidate.Smtp.Host; account.SmtpPort = candidate.Smtp.Port; account.SmtpSecurity = candidate.Smtp.Security;
        account.DiscoverySource = candidate.Source; account.Status = MailAccountStatus.Active; account.UpdatedAt = now; account.LastAuthenticatedAt = now;
        var credential = account.Credentials.SingleOrDefault(x => x.AuthenticationMethod == authentication.Type);
        if (credential is null) { credential = new MailCredential { Id = Guid.NewGuid(), MailAccount = account, AuthenticationMethod = authentication.Type, Provider = candidate.Provider, CreatedAt = now }; account.Credentials.Add(credential); }
        credential.EncryptedMaterial = protector.Protect(authentication.Password); credential.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        var session = await sessions.CreateAsync(account.Id, deviceIdentifier, TimeSpan.FromDays(30), cancellationToken);
        await syncQueue.EnqueueAsync(account.Id, cancellationToken);
        var access = jwt.Issue(account.Id);
        return new(access.Token, session.Token, account.Id, access.ExpiresAt);
    }
}
