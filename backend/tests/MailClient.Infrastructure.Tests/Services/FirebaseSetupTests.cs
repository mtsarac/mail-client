using System.Security.Cryptography;
using FirebaseAdmin;
using MailClient.Infrastructure.Push;

namespace MailClient.Infrastructure.Tests.Services;

public sealed class FirebaseSetupTests : IDisposable
{
    private readonly List<string> _apps = [];
    private readonly List<string> _files = [];
    private readonly string? _savedEnv = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");

    [Fact]
    public void Disabled_SkipsEverything()
    {
        new FirebaseOptions
        {
            Enabled = false,
            ProjectId = "",
            CredentialsPath = "/nonexistent/sa.json"
        }.Validate();
    }

    [Fact]
    public void ValidServiceAccount_Initializes()
    {
        var path = WriteServiceAccount(CreatePrivateKeyPem());
        var name = TrackApp();

        FirebaseSetup.EnsureInitialized(
            new FirebaseOptions { Enabled = true, ProjectId = "demo-project", CredentialsPath = path }, name);

        Assert.NotNull(FirebaseApp.GetInstance(name));
    }

    [Fact]
    public void MissingProjectId_Fails()
    {
        var path = WriteServiceAccount(CreatePrivateKeyPem());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            FirebaseSetup.EnsureInitialized(
                new FirebaseOptions { Enabled = true, ProjectId = "", CredentialsPath = path }, TrackApp()));

        Assert.DoesNotContain("PRIVATE", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingExplicitCredentials_Fails()
    {
        Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", "/nonexistent/env-sa.json");

        Assert.Throws<InvalidOperationException>(() =>
            FirebaseSetup.EnsureInitialized(
                new FirebaseOptions { Enabled = true, ProjectId = "demo", CredentialsPath = "/nonexistent/sa.json" },
                TrackApp()));
    }

    [Fact]
    public void MalformedCredentials_Fails()
    {
        var path = TrackFile();
        File.WriteAllText(path, "not-json{{{");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            FirebaseSetup.EnsureInitialized(
                new FirebaseOptions { Enabled = true, ProjectId = "demo", CredentialsPath = path }, TrackApp()));

        Assert.Contains("not valid JSON", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not-json", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NonServiceAccountCredentials_Fails()
    {
        var path = TrackFile();
        File.WriteAllText(path, """{"type":"authorized_user","client_id":"x","client_secret":"y","refresh_token":"z"}""");

        Assert.Throws<InvalidOperationException>(() =>
            FirebaseSetup.EnsureInitialized(
                new FirebaseOptions { Enabled = true, ProjectId = "demo", CredentialsPath = path }, TrackApp()));
    }

    [Fact]
    public void InvalidPrivateKey_FailsWithoutLeakingSecrets()
    {
        var path = WriteServiceAccount("not-a-private-key");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            FirebaseSetup.EnsureInitialized(
                new FirebaseOptions { Enabled = true, ProjectId = "demo", CredentialsPath = path }, TrackApp()));

        Assert.Contains("private key", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not-a-private-key", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingPrivateKeyField_Fails()
    {
        var path = TrackFile();
        File.WriteAllText(path,
            """{"type":"service_account","project_id":"demo","client_email":"a@b.iam.gserviceaccount.com"}""");

        Assert.Throws<InvalidOperationException>(() =>
            FirebaseSetup.EnsureInitialized(
                new FirebaseOptions { Enabled = true, ProjectId = "demo", CredentialsPath = path }, TrackApp()));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", _savedEnv);
        foreach (var name in _apps)
        {
            try
            {
                FirebaseApp.GetInstance(name)?.Delete();
            }
            catch (InvalidOperationException)
            {
            }
        }

        foreach (var file in _files)
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }

    private string TrackApp()
    {
        var name = "test-" + Guid.NewGuid().ToString("N");
        _apps.Add(name);
        return name;
    }

    private string TrackFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fb-test-{Guid.NewGuid():N}.json");
        _files.Add(path);
        return path;
    }

    private string WriteServiceAccount(string privateKeyPem)
    {
        var path = TrackFile();
        var pem = privateKeyPem.Replace("\r", "").Replace("\n", "\\n");
        File.WriteAllText(path,
            $$$"""{"type":"service_account","project_id":"demo-project","private_key_id":"1","private_key":"{{{pem}}}","client_email":"test@demo-project.iam.gserviceaccount.com","client_id":"1","token_uri":"https://oauth2.googleapis.com/token"}""");
        return path;
    }

    private static string CreatePrivateKeyPem()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportPkcs8PrivateKeyPem();
    }
}
