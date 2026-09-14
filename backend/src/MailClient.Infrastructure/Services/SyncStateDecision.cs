namespace MailClient.Infrastructure.Services;

public static class SyncStateDecision
{
    public static bool RequiresReset(uint storedUidValidity, uint serverUidValidity) =>
        storedUidValidity != 0 && storedUidValidity != serverUidValidity;
}
