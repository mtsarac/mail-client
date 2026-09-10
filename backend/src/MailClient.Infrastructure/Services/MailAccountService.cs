using MailClient.Application.Interfaces;
using MailClient.Application.Network;
using MailClient.Application.Validation;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MailClient.Infrastructure.Services;

public sealed class MailAccountService(
    AppDbContext db,
    ICredentialProtector credentials,
    IHostEnvironment environment,
    IMailConnectivityTester tester,
    IOutboundHostValidator hosts,
    ILogger<MailAccountService> logger) : IMailAccountService
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
        Validate(request, passwordRequired: true);
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
        logger.LogInformation("User {UserId} created mail account {AccountId}.", userId, account.Id);
        return ToResponse(account);
    }

    public async Task<MailAccountResponse?> UpdateAsync(Guid userId, Guid accountId, UpdateMailAccountRequest request, CancellationToken cancellationToken)
    {
        Validate(request);
        var account = await FindOwnedAsync(userId, accountId, cancellationToken);
        if (account is null) return null;

        account.EmailAddress = request.EmailAddress.Trim().ToLowerInvariant();
        account.DisplayName = request.DisplayName.Trim();
        account.Username = request.Username.Trim();
        if (request.Password is not null)
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
        logger.LogInformation("User {UserId} updated mail account {AccountId}.", userId, accountId);
        return ToResponse(account);
    }

    public async Task<bool> DeleteAsync(Guid userId, Guid accountId, CancellationToken cancellationToken)
    {
        var account = await FindOwnedAsync(userId, accountId, cancellationToken);
        if (account is null) return false;
        db.MailAccounts.Remove(account);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("User {UserId} deleted mail account {AccountId}.", userId, accountId);
        return true;
    }

    public async Task<MailAccountTestResponse?> TestAsync(Guid userId, Guid accountId, CancellationToken cancellationToken)
    {
        var account = await FindOwnedAsync(userId, accountId, cancellationToken);
        if (account is null) return null;

        try
        {
            var password = credentials.Unprotect(account.EncryptedPassword);
            await tester.TestImapAsync(
                new MailServerEndpoint(account.ImapHost, account.ImapPort, account.ImapSecurity),
                account.Username, password, cancellationToken);
            await tester.TestSmtpAsync(
                new MailServerEndpoint(account.SmtpHost, account.SmtpPort, account.SmtpSecurity),
                account.Username, password, cancellationToken);
            logger.LogInformation("Mail connection test succeeded for account {AccountId} of user {UserId}.", accountId, userId);
            return new MailAccountTestResponse(true, "IMAP and SMTP connections succeeded.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MailConnectionException ex)
        {
            logger.LogWarning(ex,
                "Mail connection test failed ({Failure}) for account {AccountId} of user {UserId}.",
                ex.Failure, accountId, userId);
            return new MailAccountTestResponse(false, ex.Message);
        }
    }

    private async Task<MailAccount?> FindOwnedAsync(Guid userId, Guid accountId, CancellationToken cancellationToken) =>
        await db.MailAccounts.SingleOrDefaultAsync(account => account.Id == accountId && account.UserId == userId, cancellationToken);

    private void Validate(MailAccountRequest request, bool passwordRequired)
    {
        var errors = new Dictionary<string, string[]>();
        RequestValidator.RequireEmail(request?.EmailAddress, "emailAddress", errors);
        RequestValidator.RequireDisplayName(request?.DisplayName, "displayName", 250, errors);
        if (string.IsNullOrWhiteSpace(request?.Username))
            errors["username"] = ["Username is required."];
        if (passwordRequired)
            RequestValidator.RequireMailboxPassword(request?.Password, "password", errors);
        RequestValidator.RequireHost(request?.ImapHost, "imapHost", errors);
        RequestValidator.RequirePort(request?.ImapPort ?? 0, "imapPort", errors);
        RequestValidator.RequireHost(request?.SmtpHost, "smtpHost", errors);
        RequestValidator.RequirePort(request?.SmtpPort ?? 0, "smtpPort", errors);
        if (request is not null)
        {
            RequestValidator.RequireDefinedEnum(request.ImapSecurity, "imapSecurity", errors);
            RequestValidator.RequireDefinedEnum(request.SmtpSecurity, "smtpSecurity", errors);
        }
        CheckLiteralHost(request?.ImapHost, "imapHost", errors);
        CheckLiteralHost(request?.SmtpHost, "smtpHost", errors);
        if (!environment.IsDevelopment() && !environment.IsEnvironment("Test") && request is not null
            && (request.ImapSecurity == MailSecurity.None || request.SmtpSecurity == MailSecurity.None))
            errors["security"] = ["MailSecurity.None is allowed only in Development or Test."];
        RequestValidator.ThrowIfInvalid(errors);
    }

    private void Validate(UpdateMailAccountRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        RequestValidator.RequireEmail(request?.EmailAddress, "emailAddress", errors);
        RequestValidator.RequireDisplayName(request?.DisplayName, "displayName", 250, errors);
        if (string.IsNullOrWhiteSpace(request?.Username))
            errors["username"] = ["Username is required."];
        if (request?.Password is not null)
            RequestValidator.RequireMailboxPassword(request.Password, "password", errors);
        RequestValidator.RequireHost(request?.ImapHost, "imapHost", errors);
        RequestValidator.RequirePort(request?.ImapPort ?? 0, "imapPort", errors);
        RequestValidator.RequireHost(request?.SmtpHost, "smtpHost", errors);
        RequestValidator.RequirePort(request?.SmtpPort ?? 0, "smtpPort", errors);
        if (request is not null)
        {
            RequestValidator.RequireDefinedEnum(request.ImapSecurity, "imapSecurity", errors);
            RequestValidator.RequireDefinedEnum(request.SmtpSecurity, "smtpSecurity", errors);
        }
        CheckLiteralHost(request?.ImapHost, "imapHost", errors);
        CheckLiteralHost(request?.SmtpHost, "smtpHost", errors);
        if (!environment.IsDevelopment() && !environment.IsEnvironment("Test") && request is not null
            && (request.ImapSecurity == MailSecurity.None || request.SmtpSecurity == MailSecurity.None))
            errors["security"] = ["MailSecurity.None is allowed only in Development or Test."];
        RequestValidator.ThrowIfInvalid(errors);
    }

    private void CheckLiteralHost(string? host, string field, Dictionary<string, string[]> errors)
    {
        if (errors.ContainsKey(field))
            return;

        var check = hosts.CheckLiteralHost(host);
        if (!check.Allowed)
        {
            logger.LogDebug("Rejected {Field} value at validation: {Reason}.", field, check.Reason);
            errors[field] = ["Mail host is not allowed."];
        }
    }

    private static MailAccountResponse ToResponse(MailAccount account) => new(
        account.Id, account.EmailAddress, account.DisplayName, account.Username,
        account.ImapHost, account.ImapPort, account.ImapSecurity,
        account.SmtpHost, account.SmtpPort, account.SmtpSecurity,
        account.SaveSentCopy, account.IsActive);
}
