using MailClient.Application.Interfaces;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Security;
using MailClient.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace MailClient.Infrastructure.Tests.Services;

public class MailAccountServiceTests
{
    [Fact]
    public async Task CreateAsync_ProtectsPasswordAndDoesNotReturnIt()
    {
        var userId = Guid.NewGuid();
        await using var db = CreateDb();
        var protector = CreateProtector();
        var service = new MailAccountService(db, protector, new TestEnvironment("Development"));
        var request = Request("mailbox-password");

        var result = await service.CreateAsync(userId, request, CancellationToken.None);

        var stored = await db.MailAccounts.SingleAsync();
        Assert.NotEqual("mailbox-password", stored.EncryptedPassword);
        Assert.Equal("mailbox-password", protector.Unprotect(stored.EncryptedPassword));
        Assert.Equal("account@example.com", result.EmailAddress);
    }

    [Fact]
    public async Task GetAsync_DoesNotReturnAnotherUsersAccount()
    {
        await using var db = CreateDb();
        var service = new MailAccountService(db, CreateProtector(), new TestEnvironment("Development"));
        var account = await service.CreateAsync(Guid.NewGuid(), Request("password"), CancellationToken.None);

        var result = await service.GetAsync(Guid.NewGuid(), account.Id, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task CreateAsync_RejectsPlainSecurityOutsideDevelopment()
    {
        await using var db = CreateDb();
        var service = new MailAccountService(db, CreateProtector(), new TestEnvironment("Production"));

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(Guid.NewGuid(), Request("password"), CancellationToken.None));
    }

    private static AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options);

    private static ICredentialProtector CreateProtector() => new DataProtectionCredentialProtector(
        DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()))));

    private static MailAccountRequest Request(string password) => new(
        "account@example.com", "Account", "account@example.com", password,
        "localhost", 3143, MailSecurity.None,
        "localhost", 3025, MailSecurity.None, true);

    private sealed class TestEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "MailClient.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
