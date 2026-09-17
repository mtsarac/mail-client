namespace MailClient.Infrastructure.Storage;

public enum StorageProvider
{
    /// <summary>Local filesystem. Single instance, or multiple instances sharing one volume.</summary>
    Local,

    /// <summary>S3-compatible object storage. Required for multi-instance deployments without a shared volume.</summary>
    S3
}

/// <summary>
/// Static deployment configuration. Never stored in database-backed runtime settings, and credentials are read
/// from configuration/environment only — they are never persisted or logged.
/// </summary>
public sealed class StorageOptions
{
    public StorageProvider Provider { get; set; } = StorageProvider.Local;
    public S3StorageOptions S3 { get; set; } = new();

    public void Validate()
    {
        if (Provider == StorageProvider.S3)
            S3.Validate();
    }
}

public sealed class S3StorageOptions
{
    public string Bucket { get; set; } = "";

    /// <summary>Custom endpoint for S3-compatible services (MinIO, Ceph). Empty uses the AWS endpoint for the region.</summary>
    public string ServiceUrl { get; set; } = "";
    public string Region { get; set; } = "";

    /// <summary>Optional key prefix so one bucket can host several environments.</summary>
    public string Prefix { get; set; } = "";

    /// <summary>Required by most non-AWS S3 implementations.</summary>
    public bool ForcePathStyle { get; set; }

    /// <summary>Optional. When empty the ambient AWS credential chain (environment, IAM role) is used.</summary>
    public string AccessKeyId { get; set; } = "";
    public string SecretAccessKey { get; set; } = "";

    public bool HasStaticCredentials =>
        !string.IsNullOrWhiteSpace(AccessKeyId) && !string.IsNullOrWhiteSpace(SecretAccessKey);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Bucket))
            throw new InvalidOperationException("Storage:S3:Bucket must be provided when Storage:Provider is S3.");
        if (string.IsNullOrWhiteSpace(ServiceUrl) && string.IsNullOrWhiteSpace(Region))
            throw new InvalidOperationException("Storage:S3 requires either ServiceUrl or Region.");
        if (!string.IsNullOrWhiteSpace(ServiceUrl) && !Uri.TryCreate(ServiceUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException("Storage:S3:ServiceUrl must be an absolute URI.");
        if (string.IsNullOrWhiteSpace(AccessKeyId) != string.IsNullOrWhiteSpace(SecretAccessKey))
            throw new InvalidOperationException("Storage:S3 requires both AccessKeyId and SecretAccessKey, or neither.");
    }
}
