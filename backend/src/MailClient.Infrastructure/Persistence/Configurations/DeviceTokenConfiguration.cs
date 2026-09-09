using MailClient.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MailClient.Infrastructure.Persistence.Configurations;

public class DeviceTokenConfiguration : IEntityTypeConfiguration<DeviceToken>
{
    public void Configure(EntityTypeBuilder<DeviceToken> builder)
    {
        builder.HasIndex(deviceToken => deviceToken.Token).IsUnique();

        builder.Property(deviceToken => deviceToken.UserId).HasMaxLength(100);
        builder.Property(deviceToken => deviceToken.Token).HasMaxLength(500);
        builder.Property(deviceToken => deviceToken.Platform).HasMaxLength(30);
    }
}
