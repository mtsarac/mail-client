using Amazon.S3;
using Amazon.S3.Model;
using MailClient.Application.Mail;

namespace MailClient.Infrastructure.Storage;

/// <summary>
/// S3-compatible attachment storage (AWS S3, MinIO, Ceph) for multi-instance deployments. Object keys mirror the
/// local filesystem layout, so stored attachment paths stay portable between backends.
/// </summary>
public sealed class S3AttachmentStorage(IAmazonS3 client, StorageOptions options) : IFileStorage
{
    public async Task<StoredFile> SaveAsync(
        Guid accountId,
        Guid mailId,
        Guid attachmentId,
        Func<Stream, CancellationToken, Task> write,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var relativePath = AttachmentPath.Relative(accountId, mailId, attachmentId);
        using var buffer = new MemoryStream();
        await using (var bounded = new BoundedWriteStream(buffer, maxBytes))
            await write(bounded, cancellationToken);
        buffer.Position = 0;
        await client.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = options.S3.Bucket,
                Key = Key(relativePath),
                InputStream = buffer,
                AutoCloseStream = false
            },
            cancellationToken);
        return new StoredFile(relativePath, buffer.Length);
    }

    public async Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken)
    {
        var response = await client.GetObjectAsync(options.S3.Bucket, Key(relativePath), cancellationToken);
        return response.ResponseStream;
    }

    public async Task DeleteAsync(string relativePath, CancellationToken cancellationToken)
    {
        try
        {
            await client.DeleteObjectAsync(options.S3.Bucket, Key(relativePath), cancellationToken);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
        }
    }

    public async Task DeleteAccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var prefix = Key(AttachmentPath.AccountPrefix(accountId));
        string? continuationToken = null;
        do
        {
            var listed = await client.ListObjectsV2Async(
                new ListObjectsV2Request { BucketName = options.S3.Bucket, Prefix = prefix, ContinuationToken = continuationToken },
                cancellationToken);
            if (listed.S3Objects is { Count: > 0 })
            {
                await client.DeleteObjectsAsync(
                    new DeleteObjectsRequest
                    {
                        BucketName = options.S3.Bucket,
                        Objects = listed.S3Objects.Select(item => new KeyVersion { Key = item.Key }).ToList()
                    },
                    cancellationToken);
            }

            continuationToken = listed.IsTruncated == true ? listed.NextContinuationToken : null;
        }
        while (continuationToken is not null);
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            await client.ListObjectsV2Async(
                new ListObjectsV2Request { BucketName = options.S3.Bucket, MaxKeys = 1 },
                cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is AmazonS3Exception or HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private string Key(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        return string.IsNullOrWhiteSpace(options.S3.Prefix)
            ? normalized
            : $"{options.S3.Prefix.Trim('/')}/{normalized}";
    }
}

public static class AttachmentPath
{
    public static string Relative(Guid accountId, Guid mailId, Guid attachmentId) =>
        Path.Combine("attachments", accountId.ToString("N"), mailId.ToString("N"), attachmentId.ToString("N"));

    public static string AccountPrefix(Guid accountId) =>
        Path.Combine("attachments", accountId.ToString("N"));
}

/// <summary>Builds the S3 client for the configured endpoint. Credentials come from configuration or the ambient AWS chain.</summary>
public static class S3StorageFactory
{
    public static IAmazonS3 Create(S3StorageOptions options)
    {
        var config = new AmazonS3Config { ForcePathStyle = options.ForcePathStyle };
        if (!string.IsNullOrWhiteSpace(options.ServiceUrl))
            config.ServiceURL = options.ServiceUrl;
        if (!string.IsNullOrWhiteSpace(options.Region))
            config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(options.Region);
        return options.HasStaticCredentials
            ? new AmazonS3Client(options.AccessKeyId, options.SecretAccessKey, config)
            : new AmazonS3Client(config);
    }
}
