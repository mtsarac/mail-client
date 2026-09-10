namespace MailClient.Infrastructure.Services;

internal static class SyncStateDecision
{
    public static bool RequiresReset(uint storedUidValidity, uint serverUidValidity) =>
        storedUidValidity != 0 && storedUidValidity != serverUidValidity;
}
