using MailClient.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

// EF mapping for SendOperation: unique user+idempotency-key index and limits.
namespace MailClient.Infrastructure.Persistence.Configurations;

public class SendOperationConfiguration : IEntityTypeConfiguration<SendOperation>
{
    public void Configure(EntityTypeBuilder<SendOperation> builder)
    {
        builder.HasIndex(operation => new { operation.UserId, operation.IdempotencyKey }).IsUnique();
        builder.Property(operation => operation.IdempotencyKey).HasMaxLength(200);
        builder.Property(operation => operation.Fingerprint).HasMaxLength(64);
        builder.Property(operation => operation.Warning).HasMaxLength(500);

        builder.HasOne(operation => operation.User)
            .WithMany()
            .HasForeignKey(operation => operation.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
