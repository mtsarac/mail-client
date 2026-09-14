using System.Text.Json;

namespace MailClient.Api.Tests;

public sealed class LanDevelopmentTests
{
    [Fact]
    public void LanHttpProfile_BindsAllInterfacesOverPlainHttp()
    {
        var profile = Profile("lan-http");

        Assert.Equal("Project", profile.GetProperty("commandName").GetString());
        Assert.Equal("http://0.0.0.0:5223", profile.GetProperty("applicationUrl").GetString());
        Assert.False(profile.GetProperty("launchBrowser").GetBoolean());
        Assert.Equal("Development",
            profile.GetProperty("environmentVariables").GetProperty("ASPNETCORE_ENVIRONMENT").GetString());
        Assert.DoesNotContain("https", profile.GetProperty("applicationUrl").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LocalhostProfiles_Preserved()
    {
        Assert.Equal("http://localhost:5223", Profile("http").GetProperty("applicationUrl").GetString());
        Assert.Equal("https://localhost:7074;http://localhost:5223",
            Profile("https").GetProperty("applicationUrl").GetString());
    }

    private static JsonElement Profile(string name)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(LaunchSettingsPath()));
        Assert.True(document.RootElement.GetProperty("profiles").TryGetProperty(name, out var profile),
            $"launchSettings.json must contain a '{name}' profile.");
        return profile.Clone();
    }

    private static string LaunchSettingsPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "MailClient.Api", "Properties", "launchSettings.json");
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }
        Assert.Fail("Could not locate src/MailClient.Api/Properties/launchSettings.json from the test output directory.");
        throw new InvalidOperationException("Unreachable.");
    }
}
