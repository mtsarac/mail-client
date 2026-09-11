using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MailClient.Api.Auth;
using MailClient.Infrastructure.Push;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace MailClient.Api.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void FirebaseOptions_Disabled_SkipsValidation()
    {
        new FirebaseOptions { Enabled = false }.Validate();
    }

    [Fact]
    public void FirebaseOptions_EnabledWithoutProjectId_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new FirebaseOptions { Enabled = true, ProjectId = "" }.Validate());
    }

    [Fact]
    public void FirebaseOptions_EnabledWithoutCredentials_Throws()
    {
        var previous = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
        Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", "/nonexistent/env-sa.json");
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                new FirebaseOptions { Enabled = true, ProjectId = "demo", CredentialsPath = "/nonexistent/sa.json" }.Validate());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", previous);
        }
    }

    [Fact]
    public void DataProtectionSetup_DevelopmentWithoutCertificate_ReturnsNull()
    {
        var services = new ServiceCollection();
        var builder = services.AddDataProtection();

        var result = DataProtectionSetup.Configure(builder, EmptyConfig(), new TestEnvironment("Development"));

        Assert.Null(result);
    }

    [Fact]
    public void DataProtectionSetup_ProductionWithoutCertificate_Throws()
    {
        var services = new ServiceCollection();
        var builder = services.AddDataProtection();

        Assert.Throws<InvalidOperationException>(() =>
            DataProtectionSetup.Configure(builder, EmptyConfig(), new TestEnvironment("Production")));
    }

    [Fact]
    public void DataProtectionSetup_MissingCertificateFile_Throws()
    {
        var services = new ServiceCollection();
        var builder = services.AddDataProtection();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataProtection:CertificatePath"] = "/nonexistent/cert.pfx"
        }).Build();

        Assert.Throws<InvalidOperationException>(() =>
            DataProtectionSetup.Configure(builder, config, new TestEnvironment("Development")));
    }

    [Fact]
    public void DataProtectionSetup_ValidCertificate_ProtectsKeys()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dp-test-{Guid.NewGuid():N}.pfx");
        const string password = "test-password-1";
        using (var rsa = RSA.Create(2048))
        {
            var request = new CertificateRequest("CN=MailClientTest", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, password));
        }

        try
        {
            var services = new ServiceCollection();
            var builder = services.AddDataProtection();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataProtection:CertificatePath"] = path,
                ["DataProtection:CertificatePassword"] = password
            }).Build();

            var certificate = DataProtectionSetup.Configure(builder, config, new TestEnvironment("Development"));

            Assert.NotNull(certificate);
            Assert.True(certificate.HasPrivateKey);
            certificate.Dispose();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

    private sealed class TestEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "MailClient.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
