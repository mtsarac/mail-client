using MailClient.Application.Mail;
using MailClient.Domain;

namespace MailClient.Api.Endpoints;

internal static class ApiFailureMapper
{
    internal static (string Code, int Status) MapFailure(Exception? exception) => exception switch
    {
        MailConnectionException { Failure: MailConnectionFailure.Authentication, Operation: "ValidateSmtp" } => ("mail_smtp_authentication_failed", 401),
        MailConnectionException { Failure: MailConnectionFailure.Authentication } => ("mail_authentication_failed", 401),
        MailConnectionException { Failure: MailConnectionFailure.Tls } => ("mail_tls_failed", 502),
        MailConnectionException { Failure: MailConnectionFailure.Protocol } => ("mail_provider_unavailable", 502),
        MailConnectionException => ("mail_server_unreachable", 502),
        InvalidOperationException known when StatusFor(known.Message) is { } status => (known.Message, status),
        _ => ("unexpected_error", 500)
    };

    internal static int? StatusFor(string code) => code switch
    {
        "mail_authentication_failed" or "mail_smtp_authentication_failed" => 401,
        "invalid_recipient" or "recipient_required" or "body_required" or "body_too_large" or "too_many_attachments"
            or "attachment_too_large" or "message_not_constructible" or "idempotency_key_required"
            or "idempotency_key_too_long" or "invalid_mail_header" or "manual_setup_invalid"
            or "invalid_email" => 400,
        "mail_server_unsafe" or "unsupported_authentication_method"
            or "oauth_not_implemented" or "discovery_invalid"
            or "oauth_provider_not_configured" or "oauth_redirect_uri_invalid"
            or "oauth_code_exchange_failed" or "oauth_state_invalid" => 422,
        "mail_account_not_found" or "mail_not_found" or "draft_not_found" => 404,
        "drafts_folder_unavailable" or "trash_folder_unavailable" or "mail_not_draft" => 422,
        "draft_delete_failed" => 502,
        "idempotency_conflict" or "send_in_progress" or "delivery_unknown" or "mailbox_changed"
            or "credential_missing" or "mail_account_needs_reauthentication" => 409,
        "provider_disabled" or "provider_existing_accounts_disabled" or "provider_new_accounts_disabled" or "authentication_method_disabled" => 403,
        "runtime_settings_conflict" => 409,
        "runtime_settings_invalid" or "management_unauthorized" => 400,
        "mail_account_disabled" => 403,
        "sync_queue_full" => 503,
        _ => null
    };
}
