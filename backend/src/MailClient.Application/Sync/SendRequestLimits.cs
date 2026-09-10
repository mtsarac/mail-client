namespace MailClient.Application.Sync;

public static class SendRequestLimits
{
    public const long MultipartOverheadBytes = 1024 * 1024;

    public static long ComputeMaxRequestBytes(MailSyncOptions options) =>
        options.MaxMessageAttachmentBytes
        + 4L * 2 * options.MaxSendBodyChars
        + MultipartOverheadBytes;
}
