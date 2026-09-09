using MailClient.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MailClient.Infrastructure.Persistence.Configurations;

public class SentMailConfiguration : IEntityTypeConfiguration<SentMail>
{
    public void Configure(EntityTypeBuilder<SentMail> builder)
    {
        builder.Property(sentMail => sentMail.ToAddress).HasMaxLength(320);
        builder.Property(sentMail => sentMail.Subject).HasMaxLength(500);
    }
}
