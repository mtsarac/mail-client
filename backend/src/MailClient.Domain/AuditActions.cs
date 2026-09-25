namespace MailClient.Domain;

public static class AuditActions
{
    public const string MailAccountDiscoverySucceeded = "mail-account.discovery-succeeded";
    public const string MailAccountDiscoveryFailed = "mail-account.discovery-failed";
    public const string MailAccountManualSetupAttempted = "mail-account.manual-setup-attempted";
    public const string MailAccountManualSetupSucceeded = "mail-account.manual-setup-succeeded";
    public const string MailAccountConnected = "mail-account.connected";
    public const string MailAccountLoggedIn = "mail-account.logged-in";
    public const string MailAccountOAuthStarted = "mail-account.oauth-started";
    public const string MailAccountAuthenticationFailed = "mail-account.authentication-failed";
    public const string MailAccountReauthenticationRequired = "mail-account.reauthentication-required";
    public const string MailSessionCreated = "mail-session.created";
    public const string MailSessionRefreshed = "mail-session.refreshed";
    public const string MailSessionRevoked = "mail-session.revoked";
    public const string MailSyncRequested = "mail.sync-requested";
    public const string MailReadStateChanged = "mail.read-state-changed";
    public const string MailSent = "mail.sent";
    public const string DraftCreated = "draft.created";
    public const string DraftUpdated = "draft.updated";
    public const string DraftDeleted = "draft.deleted";
    public const string DeviceRegistered = "device.registered";
    public const string DeviceRemoved = "device.removed";
    public const string AllowlistEmailAdded = "allowlist.email-added";
    public const string AllowlistEmailRemoved = "allowlist.email-removed";
    public const string MailAccountAccessRevoked = "mail-account.access-revoked";
    public const string MailAccountAccessRestored = "mail-account.access-restored";
    public const string MailAccountDeletedGracePeriodExpired = "mail-account.deleted-grace-period-expired";
    public const string ScheduledSendCreated = "scheduled-send.created";
    public const string ScheduledSendCancelled = "scheduled-send.cancelled";
    public const string LabelCreated = "label.created";
    public const string LabelUpdated = "label.updated";
    public const string LabelDeleted = "label.deleted";
    public const string MailFolderCreated = "mail-folder.created";
    public const string MailFolderRenamed = "mail-folder.renamed";
    public const string MailFolderDeleted = "mail-folder.deleted";
    public const string ContactCreated = "contact.created";
    public const string ContactUpdated = "contact.updated";
    public const string ContactDeleted = "contact.deleted";
    // Stored values predate the dotted naming and are kept so existing audit rows stay comparable.
    public const string RuntimeSettingsUpdated = "runtime_settings_updated";
}
