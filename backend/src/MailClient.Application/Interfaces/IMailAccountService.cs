using MailClient.Domain.Enums;

namespace MailClient.Application.Interfaces;

public interface IMailAccountService
{
    Task<IReadOnlyList<MailAccountResponse>> ListAsync(Guid userId, CancellationToken cancellationToken);
    Task<MailAccountResponse?> GetAsync(Guid userId, Guid accountId, CancellationToken cancellationToken);
    Task<MailAccountResponse> CreateAsync(Guid userId, MailAccountRequest request, CancellationToken cancellationToken);
    Task<MailAccountResponse?> UpdateAsync(Guid userId, Guid accountId, MailAccountRequest request, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(Guid userId, Guid accountId, CancellationToken cancellationToken);
    Task<MailAccountTestResponse?> TestAsync(Guid userId, Guid accountId, CancellationToken cancellationToken);
}

public sealed record MailAccountRequest(
    string EmailAddress,
    string DisplayName,
    string Username,
    string Password,
    string ImapHost,
    int ImapPort,
    MailSecurity ImapSecurity,
    string SmtpHost,
    int SmtpPort,
    MailSecurity SmtpSecurity,
    bool SaveSentCopy);

public sealed record MailAccountResponse(
    Guid Id,
    string EmailAddress,
    string DisplayName,
    string Username,
    string ImapHost,
    int ImapPort,
    MailSecurity ImapSecurity,
    string SmtpHost,
    int SmtpPort,
    MailSecurity SmtpSecurity,
    bool SaveSentCopy,
    bool IsActive);

public sealed record MailAccountTestResponse(bool Succeeded, string Message);
