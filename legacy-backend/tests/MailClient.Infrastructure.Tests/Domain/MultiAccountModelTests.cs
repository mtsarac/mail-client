using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MailClient.Infrastructure.Tests.Domain;

public class MultiAccountModelTests
{
    [Fact]
    public void MailAccount_DefaultsToTlsAndSavingSentCopy()
    {
        var account = new MailAccount();

        Assert.Equal(MailSecurity.SslOnConnect, account.ImapSecurity);
        Assert.Equal(MailSecurity.StartTls, account.SmtpSecurity);
        Assert.True(account.SaveSentCopy);
        Assert.True(account.IsActive);
    }

    [Fact]
    public void Model_DefinesRequiredUniqueIndexes()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=PostaKoprusu").Options;
        using var db = new AppDbContext(options);

        var indexes = db.Model.GetEntityTypes().SelectMany(type => type.GetIndexes()).Where(index => index.IsUnique).Select(index => string.Join(',', index.Properties.Select(property => property.Name)));

        Assert.Contains("Email", indexes);
        Assert.Contains("Token", indexes);
        Assert.Contains("MailFolderId,UidValidity,Uid", indexes);
        Assert.Contains("MailFolderId", indexes);
    }

    [Fact]
    public void User_DefaultsToPendingUser()
    {
        var user = new User();

        Assert.Equal(UserRole.User, user.Role);
        Assert.Equal(UserStatus.Pending, user.Status);
    }

    [Fact]
    public void Model_DoesNotContainSentMail()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=PostaKoprusu").Options;
        using var db = new AppDbContext(options);

        Assert.DoesNotContain(db.Model.GetEntityTypes(), type => type.ClrType.Name == "SentMail");
    }
}
