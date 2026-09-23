using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace MailClient.Api.OpenApi;

/// <summary>Adds per-operation security, problem codes, form bodies, headers and parameter docs that endpoint metadata cannot express.</summary>
public sealed class MailClientOperationFilter : IOperationFilter
{
    public const string BearerScheme = "Bearer";
    public const string ManagementScheme = "ManagementKey";

    private static readonly Dictionary<string, string> ParameterDescriptions = new()
    {
        ["folderId"] = "Folder id from GET /api/folders.",
        ["conversationId"] = "Conversation id from GET /api/conversations.",
        ["q"] = "Free-text query, e.g. 'invoice'.",
        ["search"] = "Free-text search, same as `q` on GET /api/search (subject, body, participants, attachment names).",
        ["from"] = "Sender address or name contains this text (case-insensitive).",
        ["to"] = "A To/Cc/Bcc recipient address or name contains this text (case-insensitive).",
        ["fromDate"] = "Only mail received at or after this date-time (UTC when no offset is given).",
        ["toDate"] = "Only mail received before this date-time, exclusive (UTC when no offset is given).",
        ["isRead"] = "true = read only, false = unread only, omitted = both.",
        ["flagged"] = "true = starred only.",
        ["hasAttachment"] = "true = only mail with attachments.",
        ["hasAttachments"] = "true = only mail with attachments.",
        ["page"] = "1-based page number.",
        ["pageSize"] = "Items per page (server caps it at 100).",
        ["provider"] = "OAuth provider: google or microsoft (case-insensitive).",
        ["email"] = "Allowlisted email address (URL-encoded).",
        ["sessionId"] = "Session id from GET /api/account/sessions."
    };

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var metadata = context.ApiDescription.ActionDescriptor.EndpointMetadata;
        var path = context.ApiDescription.RelativePath ?? "";

        AddSecurity(operation, context, metadata, path);
        AddProblemCodes(operation, metadata);
        AddParameterDocs(operation, path);
        var endpointName = metadata.OfType<EndpointNameMetadata>().FirstOrDefault()?.EndpointName;
        AddIdempotencyKey(operation, endpointName);
        AddFormBody(operation, endpointName);
    }

    private static void AddSecurity(OpenApiOperation operation, OperationFilterContext context, IList<object> metadata, string path)
    {
        string? scheme = null;
        if (path.StartsWith("api/management/", StringComparison.OrdinalIgnoreCase))
            scheme = ManagementScheme;
        else if (metadata.OfType<IAuthorizeData>().Any() && !metadata.OfType<IAllowAnonymous>().Any())
            scheme = BearerScheme;
        if (scheme is null)
            return;

        operation.Security ??= [];
        operation.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(scheme, context.Document)] = [] });
        operation.Responses ??= [];
        if (scheme == BearerScheme)
            operation.Responses.TryAdd("401", new OpenApiResponse { Description = "Missing, invalid or expired access token, or the mailbox was disabled or deleted." });
        else
            operation.Responses.TryAdd("404", new OpenApiResponse { Description = "Management API is disabled (Management:Enabled=false)." });
    }

    private static void AddProblemCodes(OpenApiOperation operation, IList<object> metadata)
    {
        foreach (var problem in metadata.OfType<ProblemCodesMetadata>())
        {
            var key = problem.Status.ToString();
            var text = "Stable `code` values: " + string.Join(", ", problem.Codes.Select(code => $"`{code}`")) + ".";
            if (operation.Responses is { } responses && responses.TryGetValue(key, out var response) && response is OpenApiResponse concrete)
                concrete.Description = text;
        }
    }

    private static void AddParameterDocs(OpenApiOperation operation, string path)
    {
        foreach (var parameter in operation.Parameters?.OfType<OpenApiParameter>() ?? [])
        {
            if (string.IsNullOrEmpty(parameter.Name))
                continue;
            if (string.IsNullOrEmpty(parameter.Description) && ParameterDescriptions.TryGetValue(parameter.Name, out var text))
                parameter.Description = text;
            if (parameter.Name == "id" && string.IsNullOrEmpty(parameter.Description))
                parameter.Description = path.Contains("/folders/") ? "Folder id." : path.Contains("/devices/") ? "Device id." : path.Contains("/conversations/") ? "Conversation id." : "Mail id.";
            if (parameter.Name == "action" && parameter.Schema is OpenApiSchema schema)
            {
                parameter.Description = "Bulk operation (move requires folderId in the body).";
                schema.Enum = ["read", "unread", "star", "unstar", "archive", "trash", "restore", "spam", "not-spam", "delete", "move"];
            }
            if (parameter.Name == "provider" && parameter.Schema is OpenApiSchema providerSchema)
                providerSchema.Enum = ["google", "microsoft"];
        }
    }

    private static void AddIdempotencyKey(OpenApiOperation operation, string? endpointName)
    {
        if (endpointName is not ("SendMail" or "SendDraft"))
            return;
        operation.Parameters ??= [];
        // SendMail binds the header itself (so it is already listed as optional); replace it with the documented,
        // required form. The services reject a missing key with idempotency_key_required.
        var existing = operation.Parameters.FirstOrDefault(parameter => string.Equals(parameter.Name, "Idempotency-Key", StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            operation.Parameters.Remove(existing);
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "Idempotency-Key",
            In = ParameterLocation.Header,
            Required = true,
            Description = "Client-generated unique key (e.g. a GUID, max 200 chars). Retrying with the same key never sends twice.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
            Example = JsonValue.Create("2f6c1d1e-0a4b-4c1e-9c55-6f0b3e2a7d10")
        });
    }

    private static void AddFormBody(OpenApiOperation operation, string? endpointName)
    {
        if (endpointName is not ("SendMail" or "CreateDraft" or "UpdateDraft"))
            return;
        var text = (string description, string example) => new OpenApiSchema { Type = JsonSchemaType.String, Description = description, Example = JsonValue.Create(example) };
        var list = (string description, string example) => new OpenApiSchema { Type = JsonSchemaType.Array, Description = description, Items = new OpenApiSchema { Type = JsonSchemaType.String }, Example = new JsonArray(example) };
        var schema = new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["to"] = list("Recipient addresses (repeat the field per recipient). Required to send.", "recipient@example.com"),
                ["cc"] = list("Cc addresses (repeat the field per recipient).", "cc@example.com"),
                ["bcc"] = list("Bcc addresses (repeat the field per recipient).", "bcc@example.com"),
                ["subject"] = text("Subject line.", "Hello from Swagger"),
                ["bodyHtml"] = text("HTML body. Provide bodyHtml and/or bodyText.", "<p>Hello, this is a test.</p>"),
                ["bodyText"] = text("Plain-text body. Provide bodyHtml and/or bodyText.", "Hello, this is a test."),
                ["replySourceMailId"] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid", Description = "Optional. Id of the mail being replied to or forwarded (from the compose endpoints) so threading headers are set." },
                ["attachments"] = new OpenApiSchema { Type = JsonSchemaType.Array, Description = "Optional files (up to 20). Any file part is treated as an attachment.", Items = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" } }
            }
        };
        operation.RequestBody = new OpenApiRequestBody
        {
            Required = true,
            Content = new Dictionary<string, OpenApiMediaType> { ["multipart/form-data"] = new() { Schema = schema } }
        };
    }
}
