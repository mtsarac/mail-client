using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using MailClient.Api.Endpoints;
using MailClient.Application.Accounts;
using MailClient.Application.Runtime;
using MailClient.Domain.Enums;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace MailClient.Api.OpenApi;

/// <summary>Adds fake request examples, field descriptions and required/nullable accuracy for record DTOs. Never changes JSON shape.</summary>
public sealed class MailClientSchemaFilter : ISchemaFilter
{
    private const string Guid1 = "0f8fad5b-d9cb-469f-a165-70867728950e";
    private const string Guid2 = "7c9e6679-7425-40de-944b-e07fc1f90ae7";
    private const string ImapExample = """{"host":"imap.example.com","port":993,"security":"SslOnConnect"}""";
    private const string SmtpExample = """{"host":"smtp.example.com","port":465,"security":"SslOnConnect"}""";
    private const string AuthExample = """{"type":"AppSpecificPassword","password":"app-password-here"}""";

    private static readonly Dictionary<Type, string> Examples = new()
    {
        [typeof(DiscoverRequest)] = """{"email":"user@example.com"}""",
        [typeof(ConnectRequest)] = $$"""{"discoveryId":"discovery-id-from-discover-response","authentication":{{AuthExample}},"deviceIdentifier":"swagger-test-device"}""",
        [typeof(ManualConnectRequest)] = $$"""{"email":"user@example.com","username":"user@example.com","authentication":{{AuthExample}},"imap":{{ImapExample}},"smtp":{{SmtpExample}},"displayName":"Example User","deviceIdentifier":"swagger-test-device"}""",
        [typeof(LoginRequest)] = """{"email":"user@example.com","password":"app-password-here","deviceIdentifier":"swagger-test-device"}""",
        [typeof(AccountReconnectRequest)] = $$"""{"authentication":{{AuthExample}},"imap":{{ImapExample}},"smtp":{{SmtpExample}}}""",
        [typeof(AuthenticationInput)] = AuthExample,
        [typeof(EndpointInput)] = ImapExample,
        [typeof(RefreshRequest)] = """{"refreshToken":"refresh-token-from-previous-token-response"}""",
        [typeof(LogoutRequest)] = """{"refreshToken":"refresh-token-from-previous-token-response"}""",
        [typeof(OAuthStartRequest)] = """{"email":"user@example.com","deviceIdentifier":"swagger-test-device"}""",
        [typeof(OAuthCompleteRequest)] = """{"state":"state-from-start-response","code":"authorization-code-from-provider"}""",
        [typeof(ReadRequest)] = """{"isRead":true}""",
        [typeof(FolderOperationRequest)] = $$"""{"folderId":"{{Guid1}}"}""",
        [typeof(BulkMailOperationRequest)] = $$"""{"mailIds":["{{Guid1}}","{{Guid2}}"],"folderId":null}""",
        [typeof(DeviceRegistrationRequest)] = """{"token":"fcm-device-token-here","platform":"android","appVersion":"1.0.0","locale":"en-US"}""",
        [typeof(AddAllowlistEmailsRequest)] = """{"emails":["user@example.com","colleague@example.com"]}"""
    };

    private static readonly Dictionary<Type, string> TypeDescriptions = new()
    {
        [typeof(AuthenticationMethod)] = "Password and AppSpecificPassword are sent as `authentication.password`; OAuth2 is only used through the /api/accounts/oauth flow.",
        [typeof(MailSecurity)] = "Transport security: SslOnConnect = implicit TLS (IMAP 993 / SMTP 465), StartTls = upgrade after connecting (IMAP 143 / SMTP 587).",
        [typeof(MailProvider)] = "Detected or configured provider family.",
        [typeof(MailAccountStatus)] = "Active = usable; NeedsReauthentication = call /api/account/reconnect; ConnectionError = last sync failed; Disabled = access revoked (e.g. removed from allowlist).",
        [typeof(MailFolderType)] = "Special-use role of the folder; Custom/Unknown for user folders.",
        [typeof(AuthenticationInput)] = "Mailbox credential. `type` selects how `password` is interpreted.",
        [typeof(EndpointInput)] = "IMAP or SMTP server endpoint.",
        [typeof(RuntimeSettingsUpdateRequest)] = "Replaces the whole settings document. GET the current settings first and send back its `version` as `expectedVersion`.",
        [typeof(BulkMailOperationRequest)] = "Mail ids to act on (1-100). Which action to run is chosen by the {action} path segment.",
        [typeof(TokenResponse)] = "Session tokens. Use `accessToken` as a Bearer token; use `refreshToken` only with POST /api/auth/refresh or /api/auth/logout."
    };

    private static readonly Dictionary<string, string> PropertyDescriptions = new()
    {
        ["DiscoverResponse.DiscoveryId"] = "Opaque id valid for ~10 minutes; pass it to POST /api/accounts/connect.",
        ["DiscoverResponse.ManualSetupAvailable"] = "Always true; /api/accounts/connect-manual can be used instead of discovery.",
        ["ConnectRequest.DiscoveryId"] = "`discoveryId` returned by POST /api/accounts/discover.",
        ["ConnectRequest.DeviceIdentifier"] = "Optional label for this client/device (e.g. 'pixel-8'). Shown in GET /api/account/sessions; not a secret.",
        ["ManualConnectRequest.DeviceIdentifier"] = "Optional label for this client/device. Shown in GET /api/account/sessions; not a secret.",
        ["LoginRequest.DeviceIdentifier"] = "Optional label for this client/device. Shown in GET /api/account/sessions; not a secret.",
        ["OAuthStartRequest.DeviceIdentifier"] = "Optional label for this client/device. Shown in GET /api/account/sessions; not a secret.",
        ["MailSessionResponse.DeviceIdentifier"] = "Device label supplied at sign-in, if any.",
        ["ManualConnectRequest.Username"] = "IMAP/SMTP login name (often the full email address).",
        ["LoginRequest.Password"] = "Mailbox password or app-specific password.",
        ["AuthenticationInput.Type"] = "Password, AppSpecificPassword or OAuth2 (OAuth2 is not accepted here).",
        ["AuthenticationInput.Password"] = "Mailbox password or app-specific password. Never returned by the API.",
        ["EndpointInput.Host"] = "Public server host name. Private/loopback hosts are rejected outside Development.",
        ["EndpointInput.Port"] = "TCP port.",
        ["EndpointInput.Security"] = "SslOnConnect or StartTls.",
        ["AccountReconnectRequest.Imap"] = "Optional. Omit to keep the stored IMAP settings.",
        ["AccountReconnectRequest.Smtp"] = "Optional. Omit to keep the stored SMTP settings.",
        ["TokenResponse.AccessToken"] = "Short-lived JWT. Send as `Authorization: Bearer <accessToken>`.",
        ["TokenResponse.RefreshToken"] = "Long-lived opaque token. Rotates on every /api/auth/refresh; the previous one stops working.",
        ["TokenResponse.AccessTokenExpiresAt"] = "UTC expiry of `accessToken`.",
        ["RefreshRequest.RefreshToken"] = "Current refresh token (not the access token).",
        ["LogoutRequest.RefreshToken"] = "Refresh token of the session to revoke (not the access token).",
        ["OAuthStartRequest.Email"] = "Mailbox address being connected.",
        ["OAuthStartResponse.AuthorizationUrl"] = "Open this URL in a browser to grant access.",
        ["OAuthStartResponse.State"] = "Opaque, encrypted, time-limited value. Send it back unchanged in /complete.",
        ["OAuthCompleteRequest.State"] = "`state` from the /start response (must match the provider redirect).",
        ["OAuthCompleteRequest.Code"] = "Authorization `code` the provider appended to the redirect URI.",
        ["FolderOperationRequest.FolderId"] = "Destination folder id from GET /api/folders.",
        ["BulkMailOperationRequest.MailIds"] = "1-100 mail ids.",
        ["BulkMailOperationRequest.FolderId"] = "Destination folder id. Required for `move`, ignored otherwise.",
        ["BulkMailOperationItemResponse.Code"] = "Stable error code when `success` is false, otherwise null.",
        ["ReadRequest.IsRead"] = "true marks read, false marks unread.",
        ["RuntimeSettingsUpdateRequest.ExpectedVersion"] = "`version` from the last GET. Rejected with runtime_settings_conflict if someone else changed the settings since.",
        ["RuntimeSettingsResponse.Version"] = "Optimistic-concurrency counter; increments on every successful PUT.",
        ["AddAllowlistEmailsResponse.Added"] = "Emails that were new; duplicates and invalid values are skipped.",
        ["RuntimeWhitelistSettings.Enabled"] = "Gates account creation and access by allowlist. Only takes effect in Production.",
        ["DeviceRegistrationRequest.Token"] = "Push (FCM) registration token, max 500 chars. Treated as a secret; never returned.",
        ["DeviceRegistrationRequest.Platform"] = "e.g. android or ios (max 100 chars).",
        ["ConversationSummaryResponse.Id"] = "Conversation id; use with GET /api/conversations/{id} or the `conversationId` search filter.",
        ["ConversationMessageResponse.FolderId"] = "Folder that currently holds this message.",
        ["MailFolderResponse.Id"] = "Folder id used as `folderId` throughout the API.",
        ["MailFolderResponse.UidValidity"] = "IMAP UIDVALIDITY; changes when the server renumbers the folder."
    };

    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        var type = context.Type;
        if (schema is not OpenApiSchema concrete)
            return;
        if (TypeDescriptions.TryGetValue(type, out var description))
            concrete.Description ??= description;
        if (Examples.TryGetValue(type, out var example))
            concrete.Example = JsonNode.Parse(example);
        else if (type == typeof(RuntimeSettingsUpdateRequest))
            concrete.Example = new JsonObject
            {
                ["expectedVersion"] = 1,
                ["settings"] = JsonSerializer.SerializeToNode(new RuntimeSettings(), JsonSerializerOptions.Web)
            };

        if (concrete.Properties is null || type.IsEnum || !IsRecord(type))
        {
            DescribeProperties(concrete, type);
            return;
        }

        var constructor = type.GetConstructors().OrderByDescending(item => item.GetParameters().Length).First();
        var nullability = new NullabilityInfoContext();
        foreach (var parameter in constructor.GetParameters())
        {
            var property = type.GetProperty(parameter.Name!);
            var name = concrete.Properties.Keys.FirstOrDefault(key => string.Equals(key, JsonNamingPolicy.CamelCase.ConvertName(parameter.Name!), StringComparison.Ordinal));
            if (property is null || name is null)
                continue;
            var optional = parameter.HasDefaultValue || nullability.Create(property).ReadState == NullabilityState.Nullable;
            if (!optional)
            {
                concrete.Required ??= new HashSet<string>();
                concrete.Required.Add(name);
            }
        }

        DescribeProperties(concrete, type);
    }

    private static void DescribeProperties(OpenApiSchema schema, Type type)
    {
        foreach (var (name, property) in schema.Properties ?? new Dictionary<string, IOpenApiSchema>())
        {
            var pascal = char.ToUpperInvariant(name[0]) + name[1..];
            if (!PropertyDescriptions.TryGetValue($"{type.Name}.{pascal}", out var text))
                continue;
            switch (property)
            {
                case OpenApiSchema inline:
                    inline.Description ??= text;
                    break;
                case OpenApiSchemaReference reference:
                    reference.Description ??= text;
                    break;
            }
        }
    }

    private static bool IsRecord(Type type) => type.GetMethod("<Clone>$") is not null;
}
