// Constrained audit action names. Use these instead of ad-hoc strings.
namespace MailClient.Domain;

public static class AuditActions
{
    public const string UserRegistered = "user.registered";
    public const string UserLoggedIn = "user.logged-in";
    public const string AdminUserCreated = "admin.user-created";
    public const string AdminUserStatusChanged = "admin.user-status-changed";
    public const string AdminPasswordReset = "admin.password-reset";
    public const string MailAccountCreated = "mail-account.created";
    public const string MailAccountUpdated = "mail-account.updated";
    public const string MailAccountDeleted = "mail-account.deleted";
    public const string MailAccountTested = "mail-account.tested";
    public const string FolderRefreshRequested = "folder.refresh-requested";
    public const string FolderSyncToggled = "folder.sync-toggled";
    public const string MailSent = "mail.sent";
    public const string MailReadStateChanged = "mail.read-state-changed";
    public const string DeviceRegistered = "device.registered";
    public const string DeviceRemoved = "device.removed";
}
