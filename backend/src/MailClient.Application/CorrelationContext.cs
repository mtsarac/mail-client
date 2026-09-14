namespace MailClient.Application;

public sealed class CorrelationContext
{
    public string CorrelationId { get; private set; } = "";

    public void Initialize(string correlationId) => CorrelationId = correlationId;
}
