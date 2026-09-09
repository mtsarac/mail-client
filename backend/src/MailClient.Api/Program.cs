using MailClient.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

builder.Services.AddOpenApi();
builder.Services.AddSwaggerGen();
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .WithName("HealthCheck");
app.MapGet("/health/db", async (AppDbContext db, CancellationToken cancellationToken) =>
{
    try
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        await db.Database.CloseConnectionAsync();
        return Results.Json(
            new DbHealthResponse("Healthy", [new DbCheckStatus("postgres", "Healthy", null)]),
            statusCode: StatusCodes.Status200OK);
    }
    catch (Exception ex)
    {
        return Results.Json(
            new DbHealthResponse("Unhealthy", [new DbCheckStatus("postgres", "Unhealthy", ex.Message)]),
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
.WithName("DbHealthCheck")
.WithTags("Health")
.Produces<DbHealthResponse>(StatusCodes.Status200OK)
.Produces<DbHealthResponse>(StatusCodes.Status503ServiceUnavailable);

app.Run();

sealed record DbCheckStatus(string Name, string Status, string? Error);
sealed record DbHealthResponse(string Status, List<DbCheckStatus> Checks);
