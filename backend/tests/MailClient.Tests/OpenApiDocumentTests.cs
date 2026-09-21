using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.Swagger;

namespace MailClient.Tests;

public sealed class OpenApiDocumentTests(MailClientApiFactory factory) : IClassFixture<MailClientApiFactory>
{
    private OpenApiDocument Document() => factory.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v2");

    private static OpenApiOperation Operation(OpenApiDocument document, string path, HttpMethod method) =>
        document.Paths[path].Operations![method];

    private static bool Requires(OpenApiOperation operation, string scheme) =>
        operation.Security?.Any(requirement => requirement.Keys.Any(key => key.Reference.Id == scheme)) == true;

    [Fact]
    public void Document_GeneratesWithSecuritySchemes()
    {
        var document = Document();

        var bearer = document.Components!.SecuritySchemes!["Bearer"];
        Assert.Equal(SecuritySchemeType.Http, bearer.Type);
        Assert.Equal("bearer", bearer.Scheme);
        var management = document.Components.SecuritySchemes["ManagementKey"];
        Assert.Equal(SecuritySchemeType.ApiKey, management.Type);
        Assert.Equal("X-Management-Key", management.Name);
        Assert.Equal(ParameterLocation.Header, management.In);
    }

    [Fact]
    public void Auth_PublicEndpointsAnonymous_ProtectedEndpointsRequireBearer_ManagementRequiresKey()
    {
        var document = Document();

        var login = Operation(document, "/api/accounts/login", HttpMethod.Post);
        Assert.False(Requires(login, "Bearer") || Requires(login, "ManagementKey"));
        Assert.False(Requires(Operation(document, "/api/auth/refresh", HttpMethod.Post), "Bearer"));
        Assert.True(Requires(Operation(document, "/api/folders", HttpMethod.Get), "Bearer"));
        Assert.True(Requires(Operation(document, "/api/mails/bulk/{action}", HttpMethod.Post), "Bearer"));
        var settings = Operation(document, "/api/management/runtime-settings", HttpMethod.Get);
        Assert.True(Requires(settings, "ManagementKey"));
        Assert.False(Requires(settings, "Bearer"));
    }

    [Fact]
    public void Routes_ArePresentWithUniqueOperationIds()
    {
        var document = Document();

        foreach (var path in new[] { "/api/accounts/discover", "/api/accounts/connect", "/api/accounts/connect-manual", "/api/accounts/login", "/api/account/reconnect", "/api/auth/refresh", "/api/folders/{id}/sync", "/api/mails/send", "/api/drafts", "/api/search", "/api/mails/bulk/{action}", "/api/devices", "/api/management/whitelist/emails" })
            Assert.True(document.Paths.ContainsKey(path), path);
        var ids = document.Paths.Values.SelectMany(item => item.Operations!.Values).Select(operation => operation.OperationId).ToList();
        Assert.DoesNotContain(ids, string.IsNullOrEmpty);
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Schemas_UseStringEnums_AndDoNotLeakEntities()
    {
        var document = Document();
        var schemas = document.Components!.Schemas!;

        Assert.Equal(JsonSchemaType.String, schemas["MailSecurity"].Type);
        Assert.NotNull(schemas["LoginRequest"].Example);
        foreach (var entity in new[] { "MailAccount", "MailCredential", "MailSession", "Mail", "MailFolder" })
            Assert.DoesNotContain(entity, schemas.Keys);
    }

    [Fact]
    public void SendEndpoints_DocumentIdempotencyKeyAndMultipartBody()
    {
        var document = Document();

        foreach (var (path, method) in new[] { ("/api/mails/send", HttpMethod.Post), ("/api/drafts/{id}/send", HttpMethod.Post) })
            Assert.Contains(Operation(document, path, method).Parameters!, parameter => parameter.Name == "Idempotency-Key" && parameter.Required);
        var body = Operation(document, "/api/mails/send", HttpMethod.Post).RequestBody!.Content!["multipart/form-data"].Schema!;
        Assert.Contains("subject", body.Properties!.Keys);
    }
}
