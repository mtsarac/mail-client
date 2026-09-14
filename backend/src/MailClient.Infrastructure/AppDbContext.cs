using MailClient.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<MailAccount> MailAccounts => Set<MailAccount>();
    public DbSet<MailCredential> MailCredentials => Set<MailCredential>();
    public DbSet<MailSession> MailSessions => Set<MailSession>();
    public DbSet<MailFolder> MailFolders => Set<MailFolder>();
    public DbSet<Mail> Mails => Set<Mail>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<SyncState> SyncStates => Set<SyncState>();
    public DbSet<SyncSkippedUid> SyncSkippedUids => Set<SyncSkippedUid>();
    public DbSet<SendOperation> SendOperations => Set<SendOperation>();
    public DbSet<DeviceToken> DeviceTokens => Set<DeviceToken>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<MailAccount>().HasIndex(x => x.NormalizedEmailAddress).IsUnique();
        model.Entity<MailAccount>().Property(x => x.EmailAddress).HasMaxLength(320);
        model.Entity<MailAccount>().Property(x => x.NormalizedEmailAddress).HasMaxLength(320);
        model.Entity<MailCredential>().HasOne(x => x.MailAccount).WithMany(x => x.Credentials).HasForeignKey(x => x.MailAccountId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<MailCredential>().HasIndex(x => new { x.MailAccountId, x.AuthenticationMethod }).IsUnique();
        model.Entity<MailSession>().HasOne(x => x.MailAccount).WithMany(x => x.Sessions).HasForeignKey(x => x.MailAccountId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<MailSession>().HasIndex(x => x.RefreshTokenHash).IsUnique();
        model.Entity<MailFolder>().HasIndex(x => new { x.MailAccountId, x.FullName }).IsUnique();
        model.Entity<Mail>().HasIndex(x => new { x.MailFolderId, x.Uid }).IsUnique();
        model.Entity<Attachment>().HasIndex(x => new { x.MailAccountId, x.MailId });
        model.Entity<SyncState>().HasIndex(x => x.MailFolderId).IsUnique();
        model.Entity<SyncSkippedUid>().HasIndex(x => new { x.MailFolderId, x.Uid }).IsUnique();
        model.Entity<SendOperation>().HasIndex(x => new { x.MailAccountId, x.IdempotencyKey }).IsUnique();
        model.Entity<DeviceToken>().HasIndex(x => new { x.MailAccountId, x.Token }).IsUnique();
        model.Entity<AuditLog>().HasIndex(x => new { x.MailAccountId, x.TimestampUtc });
    }
}
