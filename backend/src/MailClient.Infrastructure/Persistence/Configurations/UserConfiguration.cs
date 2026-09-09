using MailClient.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MailClient.Infrastructure.Persistence.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.HasIndex(user => user.Email).IsUnique();
        builder.Property(user => user.Email).HasMaxLength(320);
        builder.Property(user => user.PasswordHash).HasMaxLength(500);
        builder.Property(user => user.DisplayName).HasMaxLength(250);
    }
}
