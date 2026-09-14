using MailClient.Application.Accounts;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Infrastructure.Security;

public sealed record ResolvedCredential(MailAccount Account, string Username, string Password);

public sealed class MailCredentialResolver(AppDbContext db, ICredentialProtector protector)
{
    public async Task<ResolvedCredential> ResolveAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var account = await db.MailAccounts
            .Include(x => x.Credentials)
            .SingleOrDefaultAsync(x => x.Id == accountId, cancellationToken)
            ?? throw new InvalidOperationException("mail_account_not_found");
        if (account.Status == MailAccountStatus.Disabled)
            throw new InvalidOperationException("mail_account_disabled");
        var credential = account.Credentials.SingleOrDefault(x => x.AuthenticationMethod == account.AuthenticationMethod)
            ?? throw new InvalidOperationException("credential_missing");
        if (account.AuthenticationMethod == AuthenticationMethod.OAuth2)
            throw new InvalidOperationException("oauth_not_implemented");
        return new ResolvedCredential(account, account.Username, protector.Unprotect(credential.EncryptedMaterial));
    }
}
