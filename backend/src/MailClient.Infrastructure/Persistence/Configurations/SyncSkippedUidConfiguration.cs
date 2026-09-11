using MailClient.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

// EF mapping for SyncSkippedUid: unique folder+UID skip records.
namespace MailClient.Infrastructure.Persistence.Configurations;

public class SyncSkippedUidConfiguration : IEntityTypeConfiguration<SyncSkippedUid>
{
    public void Configure(EntityTypeBuilder<SyncSkippedUid> builder)
    {
        builder.HasIndex(skip => new { skip.MailFolderId, skip.Uid }).IsUnique();
        builder.Property(skip => skip.Reason).HasMaxLength(500);

        builder.HasOne(skip => skip.MailFolder)
            .WithMany()
            .HasForeignKey(skip => skip.MailFolderId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
