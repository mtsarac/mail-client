using MailClient.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<MailAccount> MailAccounts => Set<MailAccount>();
    public DbSet<MailCredential> MailCredentials => Set<MailCredential>();
    public DbSet<MailSession> MailSessions => Set<MailSession>();
    public DbSet<MailFolder> MailFolders => Set<MailFolder>();
    public DbSet<MailClient.Domain.Entities.Mail> Mails => Set<MailClient.Domain.Entities.Mail>();
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
        model.Entity<MailFolder>().HasOne(x => x.MailAccount).WithMany(x => x.Folders).HasForeignKey(x => x.MailAccountId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<MailFolder>().HasOne(x => x.SyncState).WithOne(x => x.MailFolder).HasForeignKey<SyncState>(x => x.MailFolderId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<MailClient.Domain.Entities.Mail>().HasIndex(x => new { x.MailFolderId, x.Uid }).IsUnique();
        model.Entity<MailClient.Domain.Entities.Mail>().Property(x => x.Subject).HasMaxLength(500);
        model.Entity<MailClient.Domain.Entities.Mail>().Property(x => x.FromAddress).HasMaxLength(320);
        model.Entity<MailClient.Domain.Entities.Mail>().Property(x => x.FromDisplayName).HasMaxLength(250);
        model.Entity<MailClient.Domain.Entities.Mail>().Property(x => x.ToAddress).HasMaxLength(320);
        model.Entity<MailClient.Domain.Entities.Mail>().Property(x => x.MessageId).HasMaxLength(998);
        model.Entity<MailClient.Domain.Entities.Mail>().HasOne(x => x.MailAccount).WithMany(x => x.Mails).HasForeignKey(x => x.MailAccountId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<MailClient.Domain.Entities.Mail>().HasOne(x => x.MailFolder).WithMany(x => x.Mails).HasForeignKey(x => x.MailFolderId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<Attachment>().HasIndex(x => new { x.MailAccountId, x.MailId });
        model.Entity<Attachment>().Property(x => x.FileName).HasMaxLength(255);
        model.Entity<Attachment>().Property(x => x.ContentType).HasMaxLength(150);
        model.Entity<Attachment>().Property(x => x.ContentId).HasMaxLength(998);
        model.Entity<Attachment>().HasOne(x => x.Mail).WithMany(x => x.Attachments).HasForeignKey(x => x.MailId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<Attachment>().HasOne<MailAccount>().WithMany().HasForeignKey(x => x.MailAccountId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<SyncState>().HasIndex(x => x.MailFolderId).IsUnique();
        model.Entity<SyncState>().HasOne<MailAccount>().WithMany().HasForeignKey(x => x.MailAccountId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<SyncSkippedUid>().HasIndex(x => new { x.MailFolderId, x.Uid }).IsUnique();
        model.Entity<SyncSkippedUid>().HasOne<MailFolder>().WithMany().HasForeignKey(x => x.MailFolderId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<SyncSkippedUid>().HasOne<MailAccount>().WithMany().HasForeignKey(x => x.MailAccountId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<SendOperation>().HasIndex(x => new { x.MailAccountId, x.IdempotencyKey }).IsUnique();
        model.Entity<SendOperation>().HasOne<MailAccount>().WithMany().HasForeignKey(x => x.MailAccountId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<DeviceToken>().HasIndex(x => new { x.MailAccountId, x.Token }).IsUnique();
        model.Entity<DeviceToken>().HasOne<MailAccount>().WithMany().HasForeignKey(x => x.MailAccountId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<AuditLog>().HasIndex(x => new { x.MailAccountId, x.TimestampUtc });
        // Audit history is deliberately not destroyed with the mailbox: the row survives with a null account id.
        model.Entity<AuditLog>().HasOne<MailAccount>().WithMany().HasForeignKey(x => x.MailAccountId).OnDelete(DeleteBehavior.SetNull);
    }
}
