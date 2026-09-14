using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

// Documents the required Idempotency-Key header on the multipart send endpoint.
namespace MailClient.Api.Docs;

public sealed class IdempotencyKeyOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var path = context.ApiDescription.RelativePath;
        if (path is null || !path.EndsWith("/send", StringComparison.OrdinalIgnoreCase))
            return;
        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "Idempotency-Key",
            In = ParameterLocation.Header,
            Required = true,
            Description = "Client-generated key (1-200 chars). Replaying it returns the original result instead of resending.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String }
        });
    }
}
