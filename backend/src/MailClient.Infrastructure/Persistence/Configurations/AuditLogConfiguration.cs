using MailClient.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

// EF mapping for AuditLog: jsonb metadata, lookup index, safe column limits.
namespace MailClient.Infrastructure.Persistence.Configurations;

public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.HasIndex(log => new { log.UserId, log.TimestampUtc });
        builder.Property(log => log.Action).HasMaxLength(100);
        builder.Property(log => log.EntityType).HasMaxLength(100);
        builder.Property(log => log.EntityId).HasMaxLength(100);
        builder.Property(log => log.Metadata).HasColumnType("jsonb");
        builder.Property(log => log.CorrelationId).HasMaxLength(64);

        builder.HasOne(log => log.User)
            .WithMany()
            .HasForeignKey(log => log.UserId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
