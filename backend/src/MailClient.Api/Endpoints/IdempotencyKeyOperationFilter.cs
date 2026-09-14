using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace MailClient.Api.Endpoints;

public sealed class IdempotencyKeyOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (!string.Equals(context.ApiDescription.RelativePath, "api/mails/send", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(context.ApiDescription.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            return;
        operation.Parameters ??= [];
        if (operation.Parameters.Any(parameter =>
                string.Equals(parameter.Name, "Idempotency-Key", StringComparison.OrdinalIgnoreCase)
                && parameter.In == ParameterLocation.Header))
            return;
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "Idempotency-Key",
            In = ParameterLocation.Header,
            Required = true,
            Description = "Required idempotency key for mail send.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String }
        });
    }
}
