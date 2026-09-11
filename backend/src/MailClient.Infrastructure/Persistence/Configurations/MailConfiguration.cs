using MailClient.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

// EF mapping for Mail: unique folder+validity+UID index and list-serving indexes.
namespace MailClient.Infrastructure.Persistence.Configurations;

public class MailConfiguration : IEntityTypeConfiguration<Mail>
{
    public void Configure(EntityTypeBuilder<Mail> builder)
    {
        builder.HasIndex(mail => new { mail.MailFolderId, mail.UidValidity, mail.Uid }).IsUnique();
        builder.HasIndex(mail => new { mail.MailAccountId, mail.ReceivedAt });

        builder.Property(mail => mail.Subject).HasMaxLength(500);
        builder.Property(mail => mail.FromAddress).HasMaxLength(320);
        builder.Property(mail => mail.FromDisplayName).HasMaxLength(250);
        builder.Property(mail => mail.ToAddress).HasMaxLength(320);
        builder.Property(mail => mail.MessageId).HasMaxLength(998);

        builder.HasOne(mail => mail.MailAccount)
            .WithMany(account => account.Mails)
            .HasForeignKey(mail => mail.MailAccountId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(mail => mail.MailFolder)
            .WithMany(folder => folder.Mails)
            .HasForeignKey(mail => mail.MailFolderId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(mail => mail.Attachments)
            .WithOne(attachment => attachment.Mail)
            .HasForeignKey(attachment => attachment.MailId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
