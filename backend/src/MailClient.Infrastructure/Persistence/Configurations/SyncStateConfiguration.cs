using MailClient.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MailClient.Infrastructure.Persistence.Configurations;

public class SyncStateConfiguration : IEntityTypeConfiguration<SyncState>
{
    public void Configure(EntityTypeBuilder<SyncState> builder)
    {
        builder.HasIndex(syncState => syncState.MailFolderId).IsUnique();
        builder.HasOne(syncState => syncState.MailFolder)
            .WithOne(folder => folder.SyncState)
            .HasForeignKey<SyncState>(syncState => syncState.MailFolderId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
