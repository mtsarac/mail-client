namespace MailClient.Application.Discovery;

public sealed class MailDiscoveryOptions
{
    public int OverallTimeoutSeconds { get; init; } = 40;
    public int StrategyTimeoutSeconds { get; init; } = 8;
    public int MaxDocumentBytes { get; init; } = 256 * 1024;

    public void Validate()
    {
        if (OverallTimeoutSeconds <= 0
            || StrategyTimeoutSeconds <= 0
            || StrategyTimeoutSeconds > OverallTimeoutSeconds
            || MaxDocumentBytes <= 0)
            throw new InvalidOperationException("MailDiscovery configuration is invalid.");
    }
}
