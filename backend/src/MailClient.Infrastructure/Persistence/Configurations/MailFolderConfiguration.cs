using MailClient.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

// EF mapping for MailFolder: unique account+path index and column limits.
namespace MailClient.Infrastructure.Persistence.Configurations;

public class MailFolderConfiguration : IEntityTypeConfiguration<MailFolder>
{
    public void Configure(EntityTypeBuilder<MailFolder> builder)
    {
        builder.HasIndex(folder => new { folder.MailAccountId, folder.FullName }).IsUnique();
        builder.Property(folder => folder.Name).HasMaxLength(255);
        builder.Property(folder => folder.FullName).HasMaxLength(1_000);

        builder.HasOne(folder => folder.MailAccount)
            .WithMany(account => account.Folders)
            .HasForeignKey(folder => folder.MailAccountId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
