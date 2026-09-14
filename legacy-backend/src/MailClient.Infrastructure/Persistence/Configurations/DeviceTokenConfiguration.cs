using MailClient.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

// EF mapping for DeviceToken: globally unique token index and column limits.
namespace MailClient.Infrastructure.Persistence.Configurations;

public class DeviceTokenConfiguration : IEntityTypeConfiguration<DeviceToken>
{
    public void Configure(EntityTypeBuilder<DeviceToken> builder)
    {
        builder.HasIndex(deviceToken => deviceToken.Token).IsUnique();

        builder.Property(deviceToken => deviceToken.Token).HasMaxLength(500);
        builder.Property(deviceToken => deviceToken.Platform).HasMaxLength(30);

        builder.HasOne(deviceToken => deviceToken.User)
            .WithMany(user => user.DeviceTokens)
            .HasForeignKey(deviceToken => deviceToken.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
