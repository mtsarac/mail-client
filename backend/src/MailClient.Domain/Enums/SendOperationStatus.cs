namespace MailClient.Domain.Enums;

public enum SendOperationStatus
{
    InProgress = 0,
    FailedBeforeSend = 1,
    Sent = 2,
    SentWithCopy = 3,
    DeliveryUnknown = 4
}
