using MailClient.Domain.Entities;
using Microsoft.EntityFrameworkCore;

// EF Core context: DbSets and assembly-scanned entity configurations.
namespace MailClient.Infrastructure.Persistence;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Mail> Mails => Set<Mail>();
    public DbSet<User> Users => Set<User>();
    public DbSet<MailAccount> MailAccounts => Set<MailAccount>();
    public DbSet<MailFolder> MailFolders => Set<MailFolder>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<DeviceToken> DeviceTokens => Set<DeviceToken>();
    public DbSet<SyncState> SyncStates => Set<SyncState>();
    public DbSet<SyncSkippedUid> SyncSkippedUids => Set<SyncSkippedUid>();
    public DbSet<SendOperation> SendOperations => Set<SendOperation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
