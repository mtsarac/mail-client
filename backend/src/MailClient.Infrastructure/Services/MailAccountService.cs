using MailClient.Application.Interfaces;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace MailClient.Infrastructure.Services;

public sealed class MailAccountService(
    AppDbContext db,
    ICredentialProtector credentials,
    IHostEnvironment environment) : IMailAccountService
{
    public async Task<IReadOnlyList<MailAccountResponse>> ListAsync(Guid userId, CancellationToken cancellationToken)
    {
        return await db.MailAccounts
            .Where(account => account.UserId == userId)
            .OrderBy(account => account.EmailAddress)
            .Select(account => new MailAccountResponse(
                account.Id, account.EmailAddress, account.DisplayName, account.Username,
                account.ImapHost, account.ImapPort, account.ImapSecurity,
                account.SmtpHost, account.SmtpPort, account.SmtpSecurity,
                account.SaveSentCopy, account.IsActive))
            .ToListAsync(cancellationToken);
    }

    public async Task<MailAccountResponse?> GetAsync(Guid userId, Guid accountId, CancellationToken cancellationToken)
    {
        var account = await FindOwnedAsync(userId, accountId, cancellationToken);
        return account is null ? null : ToResponse(account);
    }

    public async Task<MailAccountResponse> CreateAsync(Guid userId, MailAccountRequest request, CancellationToken cancellationToken)
    {
        Validate(request);
        var now = DateTime.UtcNow;
        var account = new MailAccount
        {
            UserId = userId,
            EmailAddress = request.EmailAddress.Trim().ToLowerInvariant(),
            DisplayName = request.DisplayName.Trim(),
            Username = request.Username.Trim(),
            EncryptedPassword = credentials.Protect(request.Password),
            ImapHost = request.ImapHost.Trim(),
            ImapPort = request.ImapPort,
            ImapSecurity = request.ImapSecurity,
            SmtpHost = request.SmtpHost.Trim(),
            SmtpPort = request.SmtpPort,
            SmtpSecurity = request.SmtpSecurity,
            SaveSentCopy = request.SaveSentCopy,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.MailAccounts.Add(account);
        await db.SaveChangesAsync(cancellationToken);
        return ToResponse(account);
    }

    public async Task<MailAccountResponse?> UpdateAsync(Guid userId, Guid accountId, MailAccountRequest request, CancellationToken cancellationToken)
    {
        Validate(request);
        var account = await FindOwnedAsync(userId, accountId, cancellationToken);
        if (account is null) return null;

        account.EmailAddress = request.EmailAddress.Trim().ToLowerInvariant();
        account.DisplayName = request.DisplayName.Trim();
        account.Username = request.Username.Trim();
        account.EncryptedPassword = credentials.Protect(request.Password);
        account.ImapHost = request.ImapHost.Trim();
        account.ImapPort = request.ImapPort;
        account.ImapSecurity = request.ImapSecurity;
        account.SmtpHost = request.SmtpHost.Trim();
        account.SmtpPort = request.SmtpPort;
        account.SmtpSecurity = request.SmtpSecurity;
        account.SaveSentCopy = request.SaveSentCopy;
        account.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return ToResponse(account);
    }

    public async Task<bool> DeleteAsync(Guid userId, Guid accountId, CancellationToken cancellationToken)
    {
        var account = await FindOwnedAsync(userId, accountId, cancellationToken);
        if (account is null) return false;
        db.MailAccounts.Remove(account);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<MailAccountTestResponse?> TestAsync(Guid userId, Guid accountId, CancellationToken cancellationToken)
    {
        var account = await FindOwnedAsync(userId, accountId, cancellationToken);
        if (account is null) return null;

        try
        {
            var password = credentials.Unprotect(account.EncryptedPassword);
            using var imap = new ImapClient();
            await imap.ConnectAsync(account.ImapHost, account.ImapPort, MailSecurityMapper.ToSocketOptions(account.ImapSecurity), cancellationToken);
            await imap.AuthenticateAsync(account.Username, password, cancellationToken);
            await imap.DisconnectAsync(true, cancellationToken);

            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(account.SmtpHost, account.SmtpPort, MailSecurityMapper.ToSocketOptions(account.SmtpSecurity), cancellationToken);
            await smtp.AuthenticateAsync(account.Username, password, cancellationToken);
            await smtp.DisconnectAsync(true, cancellationToken);
            return new MailAccountTestResponse(true, "IMAP and SMTP connections succeeded.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return new MailAccountTestResponse(false, "IMAP or SMTP connection/authentication failed.");
        }
    }

    private async Task<MailAccount?> FindOwnedAsync(Guid userId, Guid accountId, CancellationToken cancellationToken) =>
        await db.MailAccounts.SingleOrDefaultAsync(account => account.Id == accountId && account.UserId == userId, cancellationToken);

    private void Validate(MailAccountRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.EmailAddress) || string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            throw new ArgumentException("Email address, username, and password are required.");
        if (request.ImapPort is < 1 or > 65535 || request.SmtpPort is < 1 or > 65535)
            throw new ArgumentException("Mail ports must be between 1 and 65535.");
        if (!environment.IsDevelopment() && !environment.IsEnvironment("Test") && (request.ImapSecurity == MailSecurity.None || request.SmtpSecurity == MailSecurity.None))
            throw new ArgumentException("MailSecurity.None is allowed only in Development or Test.");
    }

    private static MailAccountResponse ToResponse(MailAccount account) => new(
        account.Id, account.EmailAddress, account.DisplayName, account.Username,
        account.ImapHost, account.ImapPort, account.ImapSecurity,
        account.SmtpHost, account.SmtpPort, account.SmtpSecurity,
        account.SaveSentCopy, account.IsActive);
}
