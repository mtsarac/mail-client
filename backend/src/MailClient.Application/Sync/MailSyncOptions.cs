namespace MailClient.Application.Sync;

public sealed class MailSyncOptions
{
    public int PollIntervalSeconds { get; init; } = 30;
    public long MaxAttachmentBytes { get; init; } = 25 * 1024 * 1024;
    public long MaxMessageAttachmentBytes { get; init; } = 50 * 1024 * 1024;
}
