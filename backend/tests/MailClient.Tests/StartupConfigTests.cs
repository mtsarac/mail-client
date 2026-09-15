using MailClient.Api;

namespace MailClient.Tests;

public sealed class StartupConfigTests
{
    [Fact]
    public void Production_MissingConnectionString_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            StartupConfig.Validate(null, "/keys", "/cert.pfx", true));
    }

    [Fact]
    public void Production_EmptyConnectionString_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            StartupConfig.Validate("  ", "/keys", "/cert.pfx", true));
    }

    [Fact]
    public void Production_MissingKeyPath_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            StartupConfig.Validate("Host=db;Database=mail", null, "/cert.pfx", true));
    }

    [Fact]
    public void Production_AllPresent_DoesNotThrow()
    {
        StartupConfig.Validate("Host=db;Database=mail", "/keys", "/cert.pfx", true);
    }

    [Fact]
    public void Production_MissingCertificatePath_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            StartupConfig.Validate("Host=db;Database=mail", "/keys", null, true));
    }

    [Fact]
    public void Development_MissingValues_Allowed()
    {
        StartupConfig.Validate(null, null, null, false);
    }

    [Fact]
    public void Test_MissingValues_Allowed()
    {
        StartupConfig.Validate(null, null, null, false);
    }
}
