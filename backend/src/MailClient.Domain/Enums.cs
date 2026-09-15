namespace MailClient.Domain.Enums;

public enum AuthenticationMethod { Password, AppSpecificPassword, OAuth2 }
public enum MailProvider { Custom, Google, Microsoft, ICloud, Yahoo }
public enum MailSecurity { SslOnConnect, StartTls }
public enum DiscoverySource { KnownProvider, DnsSrv, Autoconfig, Autodiscover, Heuristic, Manual }
public enum MailAccountStatus { Active, NeedsReauthentication, ConnectionError, Disabled }
public enum SendOperationStatus { InProgress, Sent, SentWithCopy, FailedBeforeSend, DeliveryUnknown }
public enum MailFolderType { Inbox, Sent, Drafts, Trash, Junk, Archive, Custom, Unknown }
public enum ParticipantType { From, To, Cc, Bcc, ReplyTo }
public enum MailReconciliationState { None, Pending }
