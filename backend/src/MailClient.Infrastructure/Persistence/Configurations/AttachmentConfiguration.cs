using MailClient.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MailClient.Infrastructure.Persistence.Configurations;

public class AttachmentConfiguration : IEntityTypeConfiguration<Attachment>
{
    public void Configure(EntityTypeBuilder<Attachment> builder)
    {
        builder.Property(attachment => attachment.FileName).HasMaxLength(255);
        builder.Property(attachment => attachment.ContentType).HasMaxLength(150);
        builder.Property(attachment => attachment.StoragePath).HasMaxLength(1_000);
        builder.Property(attachment => attachment.ContentId).HasMaxLength(998);
    }
}
