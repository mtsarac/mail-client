using MailClient.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Infrastructure.Persistence;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Mail> Mails => Set<Mail>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<DeviceToken> DeviceTokens => Set<DeviceToken>();
    public DbSet<SyncState> SyncStates => Set<SyncState>();
    public DbSet<SentMail> SentMails => Set<SentMail>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
