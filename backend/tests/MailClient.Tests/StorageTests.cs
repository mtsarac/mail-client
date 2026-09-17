using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MailClient.Application.Mail;
using MailClient.Domain.Entities;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MailClient.Tests;

/// <summary>Runs the API against the in-memory storage adapter: endpoints must work through IFileStorage alone.</summary>
public sealed class InMemoryStorageApiFactory : MailClientApiFactory
{
    public FakeFileStorage Storage { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IFileStorage>();
            services.AddSingleton<IFileStorage>(Storage);
        });
    }
}

public sealed class StorageApiTests(InMemoryStorageApiFactory factory) : IClassFixture<InMemoryStorageApiFactory>
{
    [Fact]
    public async Task AttachmentDownload_WorksThroughTheConfiguredStorageProvider()
    {
        var (client, accountId) = await ConnectAsync();
        var mailId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        var storagePath = AttachmentPath.Relative(accountId, mailId, attachmentId);
        await factory.Storage.SaveAsync(accountId, mailId, attachmentId,
            async (stream, ct) => await stream.WriteAsync(Encoding.UTF8.GetBytes("attachment-bytes"), ct), 1024, CancellationToken.None);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Mails.Add(new Domain.Entities.Mail { Id = mailId, MailAccountId = accountId, MailFolderId = Guid.NewGuid(), Uid = 1 });
            db.Attachments.Add(new Attachment
            {
                Id = attachmentId,
                MailAccountId = accountId,
                MailId = mailId,
                FileName = "note.txt",
                ContentType = "text/plain",
                StoragePath = storagePath,
                SizeBytes = 16
            });
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync($"/api/mails/{mailId}/attachments/{attachmentId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("attachment-bytes", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AccountDeletion_RemovesAttachmentsThroughTheStorageProvider()
    {
        var (client, accountId) = await ConnectAsync();
        await factory.Storage.SaveAsync(accountId, Guid.NewGuid(), Guid.NewGuid(),
            async (stream, ct) => await stream.WriteAsync(Encoding.UTF8.GetBytes("bytes"), ct), 1024, CancellationToken.None);

        var response = await client.DeleteAsync("/api/account");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.DoesNotContain(factory.Storage.Content.Keys, key => key.Contains(accountId.ToString("N"), StringComparison.Ordinal));
    }

    private async Task<(HttpClient Client, Guid AccountId)> ConnectAsync()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/accounts/connect-manual",
            ManualRequestBuilder.Build("mail.test.invalid", "mail.test.invalid", $"storage-{Guid.NewGuid():N}@example.test"));
        response.EnsureSuccessStatusCode();
        var tokens = (await response.Content.ReadFromJsonAsync<JsonDocument>())!.RootElement;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return (client, tokens.GetProperty("mailAccountId").GetGuid());
    }
}

public sealed class StorageConfigurationTests
{
    [Fact]
    public void LocalProvider_IsTheDefaultAndNeedsNoCredentials()
    {
        var options = new StorageOptions();

        options.Validate();

        Assert.Equal(StorageProvider.Local, options.Provider);
        Assert.False(options.S3.HasStaticCredentials);
    }

    [Theory]
    [InlineData("", "eu-central-1", "", "", "Bucket")]
    [InlineData("mail", "", "", "", "ServiceUrl or Region")]
    [InlineData("mail", "", "not-a-uri", "", "absolute URI")]
    public void S3Provider_RejectsIncompleteConfiguration(string bucket, string region, string serviceUrl, string accessKeyId, string expected)
    {
        var options = new StorageOptions
        {
            Provider = StorageProvider.S3,
            S3 = new S3StorageOptions { Bucket = bucket, Region = region, ServiceUrl = serviceUrl, AccessKeyId = accessKeyId }
        };

        var error = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void S3Provider_RequiresBothCredentialPartsOrNeither()
    {
        var options = new StorageOptions
        {
            Provider = StorageProvider.S3,
            S3 = new S3StorageOptions { Bucket = "mail", Region = "eu-central-1", AccessKeyId = "key" }
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void S3Provider_AcceptsCompatibleEndpointWithAmbientCredentials()
    {
        var options = new StorageOptions
        {
            Provider = StorageProvider.S3,
            S3 = new S3StorageOptions { Bucket = "mail", ServiceUrl = "https://minio.internal:9000", ForcePathStyle = true }
        };

        options.Validate();

        Assert.False(options.S3.HasStaticCredentials);
    }

    [Fact]
    public void AttachmentPaths_AreAccountScopedAndProviderIndependent()
    {
        var accountId = Guid.NewGuid();
        var relative = AttachmentPath.Relative(accountId, Guid.NewGuid(), Guid.NewGuid());

        Assert.StartsWith(AttachmentPath.AccountPrefix(accountId), relative, StringComparison.Ordinal);
        Assert.Contains("attachments", relative, StringComparison.Ordinal);
    }
}
