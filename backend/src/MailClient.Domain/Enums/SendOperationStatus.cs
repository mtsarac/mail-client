namespace MailClient.Domain.Enums;

public enum SendOperationStatus
{
    InProgress = 0,
    Failed = 1,
    Sent = 2,
    SentWithCopy = 3
}
