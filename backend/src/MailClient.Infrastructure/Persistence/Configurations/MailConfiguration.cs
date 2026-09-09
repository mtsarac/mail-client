using MailClient.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MailClient.Infrastructure.Persistence.Configurations;

public class MailConfiguration : IEntityTypeConfiguration<Mail>
{
    public void Configure(EntityTypeBuilder<Mail> builder)
    {
        builder.HasIndex(mail => new { mail.MailboxId, mail.Uid }).IsUnique();

        builder.Property(mail => mail.Subject).HasMaxLength(500);
        builder.Property(mail => mail.FromAddress).HasMaxLength(320);
        builder.Property(mail => mail.FromDisplayName).HasMaxLength(250);
        builder.Property(mail => mail.ToAddress).HasMaxLength(320);
        builder.Property(mail => mail.MailboxId).HasMaxLength(100);

        builder.HasMany(mail => mail.Attachments)
            .WithOne(attachment => attachment.Mail)
            .HasForeignKey(attachment => attachment.MailId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
