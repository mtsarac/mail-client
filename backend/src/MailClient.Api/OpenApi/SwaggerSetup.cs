using Microsoft.AspNetCore.Http.Json;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerUI;

namespace MailClient.Api.OpenApi;

public static class SwaggerSetup
{
    public const string DocumentName = "v2";

    private const string Description = """
        Manual testing UI for the mailbox API (Development only).

        **Recommended flow**
        1. `POST /api/accounts/discover`, then `/connect` — or `/connect-manual` — to add a mailbox. `/connect` only **creates** an account; for an already registered mailbox use `/api/accounts/login`.
        2. Copy `accessToken` from the response.
        3. Click **Authorize**, paste it under **Bearer** (no `Bearer ` prefix needed).
        4. `GET /api/folders`, then `POST /api/folders/refresh` if it is empty.
        5. `POST /api/folders/{id}/sync` (asynchronous, returns 202 when queued).
        6. Try `/api/mails`, `/api/search`, mail operations and compose endpoints.
        7. When the access token expires, `POST /api/auth/refresh` with `refreshToken` (it rotates; keep the new one).

        **Auth**: 🔓 anonymous — Accounts (connect/login/OAuth) and Authentication; 🔑 **Bearer** — every other `/api` endpoint; 🛠 **ManagementKey** — `/api/management/*` (send `X-Management-Key`; Authorize it once).

        Errors are ProblemDetails with a stable `code` field. Examples use fake data only.
        """;

    public static IServiceCollection AddMailClientSwagger(this IServiceCollection services)
    {
        // Swashbuckle reads MVC JsonOptions, not the minimal-API ones, so mirror the string-enum converter to document the real wire format.
        services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(options => options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc(DocumentName, new OpenApiInfo { Title = "Mail Client API", Version = DocumentName, Description = Description });
            options.SupportNonNullableReferenceTypes();
            options.AddSecurityDefinition(MailClientOperationFilter.BearerScheme, new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "Access token from a TokenResponse: `Authorization: Bearer <accessToken>`."
            });
            options.AddSecurityDefinition(MailClientOperationFilter.ManagementScheme, new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Name = "X-Management-Key",
                Description = "Management API key (Management:ApiKey). Only for /api/management/* endpoints."
            });
            options.OperationFilter<MailClientOperationFilter>();
            options.SchemaFilter<MailClientSchemaFilter>();
            options.OrderActionsBy(api => $"{api.RelativePath}_{api.HttpMethod}");
            options.TagActionsBy(api => [api.ActionDescriptor.EndpointMetadata.OfType<Microsoft.AspNetCore.Http.Metadata.ITagsMetadata>().LastOrDefault()?.Tags.FirstOrDefault() ?? "Other"]);
            options.DocumentFilter<TagOrderDocumentFilter>();
        });
        return services;
    }

    public static void UseMailClientSwaggerUi(this WebApplication app)
    {
        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint($"/swagger/{DocumentName}/swagger.json", "Mail Client v2");
            options.EnablePersistAuthorization();
            options.EnableDeepLinking();
            options.EnableFilter();
            options.DisplayRequestDuration();
            options.DefaultModelsExpandDepth(1);
            options.DefaultModelExpandDepth(2);
            options.DocExpansion(DocExpansion.None);
            options.ConfigObject.AdditionalItems["tagsSorter"] = "alpha";
            options.ConfigObject.AdditionalItems["operationsSorter"] = "alpha";
        });
    }
}
