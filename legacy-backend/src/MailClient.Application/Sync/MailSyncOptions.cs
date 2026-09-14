// Binds and validates MailSync configuration (intervals, bounds, size caps).
namespace MailClient.Application.Sync;

public sealed class MailSyncOptions
{
    public bool Enabled { get; init; } = true;
    public int PollIntervalSeconds { get; init; } = 30;
    public int FlagSyncIntervalSeconds { get; init; } = 120;
    public int MaxMessagesPerRun { get; init; } = 100;
    public long MaxAttachmentBytes { get; init; } = 25 * 1024 * 1024;
    public long MaxMessageAttachmentBytes { get; init; } = 50 * 1024 * 1024;
    public long MaxMessageBytes { get; init; } = 100 * 1024 * 1024;
    public int MaxSendBodyChars { get; init; } = 1_000_000;

    public void Validate()
    {
        if (PollIntervalSeconds <= 0
            || FlagSyncIntervalSeconds <= 0
            || MaxMessagesPerRun <= 0
            || MaxAttachmentBytes <= 0
            || MaxMessageAttachmentBytes < MaxAttachmentBytes
            || MaxMessageBytes <= 0
            || MaxMessageBytes < MaxMessageAttachmentBytes
            || MaxSendBodyChars <= 0)
            throw new InvalidOperationException("MailSync configuration is invalid.");
    }
}
