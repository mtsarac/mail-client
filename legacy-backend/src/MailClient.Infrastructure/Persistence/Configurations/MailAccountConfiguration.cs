using MailClient.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

// EF mapping for MailAccount: unique user+email index and column limits.
namespace MailClient.Infrastructure.Persistence.Configurations;

public class MailAccountConfiguration : IEntityTypeConfiguration<MailAccount>
{
    public void Configure(EntityTypeBuilder<MailAccount> builder)
    {
        builder.HasIndex(account => new { account.UserId, account.EmailAddress }).IsUnique();
        builder.Property(account => account.EmailAddress).HasMaxLength(320);
        builder.Property(account => account.DisplayName).HasMaxLength(250);
        builder.Property(account => account.Username).HasMaxLength(320);
        builder.Property(account => account.EncryptedPassword).HasMaxLength(2_000);
        builder.Property(account => account.ImapHost).HasMaxLength(255);
        builder.Property(account => account.SmtpHost).HasMaxLength(255);

        builder.HasOne(account => account.User)
            .WithMany(user => user.MailAccounts)
            .HasForeignKey(account => account.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
