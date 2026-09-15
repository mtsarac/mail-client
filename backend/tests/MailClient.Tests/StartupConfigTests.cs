using MailClient.Api;

namespace MailClient.Tests;

public sealed class StartupConfigTests
{
    [Fact]
    public void Production_MissingConnectionString_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            StartupConfig.Validate(null, "/keys", true));
    }

    [Fact]
    public void Production_EmptyConnectionString_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            StartupConfig.Validate("  ", "/keys", true));
    }

    [Fact]
    public void Production_MissingKeyPath_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            StartupConfig.Validate("Host=db;Database=mail", null, true));
    }

    [Fact]
    public void Production_AllPresent_DoesNotThrow()
    {
        StartupConfig.Validate("Host=db;Database=mail", "/keys", true);
    }

    [Fact]
    public void Development_MissingValues_Allowed()
    {
        StartupConfig.Validate(null, null, false);
    }

    [Fact]
    public void Test_MissingValues_Allowed()
    {
        StartupConfig.Validate(null, null, false);
    }
}
