namespace MailClient.Domain;

public static class AuditActions
{
    public const string MailAccountDiscoverySucceeded = "mail-account.discovery-succeeded";
    public const string MailAccountDiscoveryFailed = "mail-account.discovery-failed";
    public const string MailAccountManualSetupAttempted = "mail-account.manual-setup-attempted";
    public const string MailAccountManualSetupSucceeded = "mail-account.manual-setup-succeeded";
    public const string MailAccountConnected = "mail-account.connected";
    public const string MailAccountAuthenticationFailed = "mail-account.authentication-failed";
    public const string MailAccountReauthenticationRequired = "mail-account.reauthentication-required";
    public const string MailSessionCreated = "mail-session.created";
    public const string MailSessionRefreshed = "mail-session.refreshed";
    public const string MailSessionRevoked = "mail-session.revoked";
    public const string MailSyncRequested = "mail.sync-requested";
    public const string MailReadStateChanged = "mail.read-state-changed";
    public const string MailSent = "mail.sent";
    public const string DeviceRegistered = "device.registered";
    public const string DeviceRemoved = "device.removed";
}
