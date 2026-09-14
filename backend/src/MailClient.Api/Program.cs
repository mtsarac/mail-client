using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using MailClient.Api.Auth;
using MailClient.Application.Accounts;
using MailClient.Application.Discovery;
using MailClient.Domain.Entities;
using MailClient.Infrastructure.Accounts;
using MailClient.Infrastructure.Authentication;
using MailClient.Infrastructure.Discovery;
using MailClient.Infrastructure.Mail;
using MailClient.Infrastructure.Network;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Security;
using MailClient.Infrastructure.Storage;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v2", new OpenApiInfo { Title = "Mail Client API", Version = "v2", Description = "MailAccount-principal API. Automatic discovery is preferred; manual setup remains SSRF/TLS/auth validated. Credentials are never returned." });
    options.AddSecurityDefinition("bearer", new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT" });
});
var connection = builder.Configuration.GetConnectionString("Default") ?? "Host=localhost;Port=5432;Database=mailclient_v2;Username=postgres;Password=postgres";
builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connection));
builder.Services.AddDataProtection();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IDnsResolver, SystemDnsResolver>();
builder.Services.AddSingleton<OutboundHostValidator>();
builder.Services.AddSingleton<DiscoveryStateStore>();
builder.Services.AddSingleton(new DnsClient.LookupClient());
builder.Services.AddHttpClient<AutoconfigDiscoveryStrategy>(client => client.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<MicrosoftAutodiscoverStrategy>(client => client.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<IMailDiscoveryStrategy, KnownProviderStrategy>();
builder.Services.AddSingleton<IMailDiscoveryStrategy, DnsSrvDiscoveryStrategy>();
builder.Services.AddTransient<IMailDiscoveryStrategy>(sp => sp.GetRequiredService<AutoconfigDiscoveryStrategy>());
builder.Services.AddTransient<IMailDiscoveryStrategy>(sp => sp.GetRequiredService<MicrosoftAutodiscoverStrategy>());
builder.Services.AddSingleton<IMailDiscoveryStrategy, HeuristicDiscoveryStrategy>();
builder.Services.AddSingleton<MailServerDiscoveryService>();
builder.Services.AddScoped<IMailConnectionValidator, MailKitConnectionValidator>();
builder.Services.AddScoped<IMailServerCandidateValidator>(sp => (MailKitConnectionValidator)sp.GetRequiredService<IMailConnectionValidator>());
builder.Services.AddScoped<ICredentialProtector, DataProtectionCredentialProtector>();
builder.Services.AddScoped<MailSessionService>();
builder.Services.AddScoped<AccountConnectionService>();
builder.Services.AddScoped<AccountScopedMailService>();
builder.Services.AddScoped<MailOperationsService>();
builder.Services.AddSingleton<InitialSyncQueue>();
builder.Services.AddHostedService<InitialSyncWorker>();
builder.Services.AddSingleton(new LocalAttachmentStorage(Path.Combine(builder.Environment.ContentRootPath, "data")));
var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new("MailClient", "MailClient", "development-only-key-change-before-production-123456789", 15);
if (jwt.Key.Length < 32) throw new InvalidOperationException("Jwt:Key must contain at least 32 characters.");
builder.Services.AddSingleton(jwt);
builder.Services.AddSingleton<IJwtTokenIssuer, JwtTokenIssuer>();
builder.Services.AddScoped<ICurrentMailAccount, CurrentMailAccount>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options => options.TokenValidationParameters = new()
{
    ValidateIssuer = true, ValidIssuer = jwt.Issuer, ValidateAudience = true, ValidAudience = jwt.Audience,
    ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)), ValidateLifetime = true,
    NameClaimType = JwtRegisteredClaimNames.Sub
});
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();
app.UseExceptionHandler(error => error.Run(async context =>
{
    var exception = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    var code = exception?.Message ?? "unexpected_error";
    context.Response.StatusCode = code switch { "mail_authentication_failed" => 401, "mail_server_unsafe" or "unsupported_authentication_method" or "invalid_email" => 422, _ => 500 };
    await Results.Problem(title: code.Replace('_', ' '), statusCode: context.Response.StatusCode, extensions: new Dictionary<string, object?> { ["code"] = code, ["correlationId"] = context.TraceIdentifier }).ExecuteAsync(context);
}));
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(options => options.SwaggerEndpoint("/swagger/v2/swagger.json", "Mail Client v2")); }

var accounts = app.MapGroup("/api/accounts").WithTags("Accounts");
accounts.MapPost("/discover", async (DiscoverRequest request, MailServerDiscoveryService discovery, DiscoveryStateStore states, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@')) return Results.ValidationProblem(new Dictionary<string, string[]> { ["email"] = ["Valid email is required."] });
    var candidate = await discovery.DiscoverAsync(request.Email.Trim(), ct);
    if (candidate is null) return Results.Problem(title: "Mail server discovery failed.", statusCode: 422, extensions: new Dictionary<string, object?> { ["code"] = "mail_discovery_failed", ["manualSetupAvailable"] = true });
    var id = states.Store(request.Email.Trim(), candidate, TimeSpan.FromMinutes(10));
    return Results.Ok(new DiscoverResponse(id, request.Email.Trim(), candidate.Provider, candidate.AuthenticationMethods, true));
}).AllowAnonymous().WithName("DiscoverMailAccount").WithSummary("Discover mail servers").WithDescription("Runs known provider, DNS SRV, autoconfig, Autodiscover, then safe heuristics. Manual setup is fallback only.").Produces<DiscoverResponse>().ProducesProblem(422).ProducesValidationProblem();
accounts.MapPost("/connect", async (ConnectRequest request, DiscoveryStateStore states, AccountConnectionService service, CancellationToken ct) => states.Take(request.DiscoveryId) is { } state ? Results.Ok(await service.ConnectAsync(state, request.Authentication, request.DeviceIdentifier, ct)) : Results.Problem(statusCode: 422, extensions: new Dictionary<string, object?> { ["code"] = "discovery_expired" })).AllowAnonymous().WithName("ConnectDiscoveredAccount").WithSummary("Connect discovered mailbox").Produces<TokenResponse>().ProducesProblem(422);
accounts.MapPost("/connect-manual", async (ManualConnectRequest request, AccountConnectionService service, CancellationToken ct) => Results.Ok(await service.ConnectManualAsync(request, ct))).AllowAnonymous().WithName("ConnectManualAccount").WithSummary("Connect mailbox with manual server settings").WithDescription("Fallback only. Host, IP, TLS, IMAP, SMTP, and credentials receive the same validation as automatic discovery.").Produces<TokenResponse>().ProducesProblem(422);

var auth = app.MapGroup("/api/auth").WithTags("Authentication");
auth.MapPost("/refresh", async (RefreshRequest request, MailSessionService sessions, IJwtTokenIssuer tokens, CancellationToken ct) =>
{
    var rotation = await sessions.RotateAsync(request.RefreshToken, TimeSpan.FromDays(30), ct);
    if (rotation is null) return Results.Problem(statusCode: 401, extensions: new Dictionary<string, object?> { ["code"] = "invalid_refresh_token" });
    var access = tokens.Issue(rotation.Value.Session.MailAccountId);
    return Results.Ok(new TokenResponse(access.Token, rotation.Value.Token, rotation.Value.Session.MailAccountId, access.ExpiresAt));
}).AllowAnonymous().WithName("RefreshSession").WithSummary("Rotate refresh session").Produces<TokenResponse>().ProducesProblem(401);
auth.MapPost("/logout", async (LogoutRequest request, MailSessionService sessions, CancellationToken ct) => await sessions.RevokeAsync(request.RefreshToken, ct) ? Results.NoContent() : Results.Problem(statusCode: 401, extensions: new Dictionary<string, object?> { ["code"] = "session_revoked" })).AllowAnonymous().WithName("LogoutSession").WithSummary("Revoke client session").Produces(204).ProducesProblem(401);

var api = app.MapGroup("/api").RequireAuthorization();
api.MapGet("/account", async (ICurrentMailAccount current, AppDbContext db, CancellationToken ct) => await db.MailAccounts.Where(x => x.Id == current.MailAccountId).Select(x => new AccountResponse(x.Id, x.EmailAddress, x.DisplayName, x.Provider, x.Status)).SingleOrDefaultAsync(ct) is { } account ? Results.Ok(account) : Results.NotFound()).WithName("GetCurrentAccount").WithSummary("Get current mailbox account");
api.MapDelete("/account", async (ICurrentMailAccount current, AppDbContext db, CancellationToken ct) => { var account = await db.MailAccounts.FindAsync([current.MailAccountId], ct); if (account is null) return Results.NotFound(); db.Remove(account); await db.SaveChangesAsync(ct); return Results.NoContent(); }).WithName("DeleteCurrentAccount").WithSummary("Delete mailbox and cached data");
api.MapGet("/folders", async (ICurrentMailAccount current, AppDbContext db, CancellationToken ct) => Results.Ok(await db.MailFolders.Where(x => x.MailAccountId == current.MailAccountId).ToListAsync(ct))).WithName("ListFolders").WithSummary("List mailbox folders").Produces<List<MailFolder>>();
api.MapPost("/folders/refresh", async (ICurrentMailAccount current, InitialSyncQueue queue, CancellationToken ct) => { await queue.EnqueueAsync(current.MailAccountId, ct); return Results.Accepted(); }).WithName("RefreshFolders").WithSummary("Refresh mailbox folders").Produces(202);
api.MapPost("/folders/{id:guid}/sync", async (Guid id, ICurrentMailAccount current, MailOperationsService service, CancellationToken ct) => await service.SyncFolderAsync(current.MailAccountId, id, ct) ? Results.Accepted() : Results.NotFound()).WithName("SyncFolder").WithSummary("Request folder synchronization").Produces(202).Produces(404);
api.MapGet("/mails", async (ICurrentMailAccount current, AppDbContext db, CancellationToken ct) => Results.Ok(await db.Mails.Where(x => x.MailAccountId == current.MailAccountId).OrderByDescending(x => x.ReceivedAt).Take(100).ToListAsync(ct))).WithName("ListMails").WithSummary("List current mailbox mail").Produces<List<MailClient.Domain.Entities.Mail>>();
api.MapGet("/mails/{id:guid}", async (Guid id, ICurrentMailAccount current, AccountScopedMailService service, CancellationToken ct) => await service.GetAsync(current.MailAccountId, id, ct) is { } mail ? Results.Ok(mail) : Results.NotFound()).WithName("GetMail").WithSummary("Get mailbox mail").Produces<MailClient.Domain.Entities.Mail>().Produces(404);
api.MapPatch("/mails/{id:guid}/read", async (Guid id, ReadRequest request, ICurrentMailAccount current, AccountScopedMailService service, CancellationToken ct) => await service.SetReadAsync(current.MailAccountId, id, request.IsRead, ct) ? Results.NoContent() : Results.NotFound()).WithName("SetMailReadState").WithSummary("Change mail read state").Produces(204).Produces(404);
api.MapGet("/mails/{mailId:guid}/attachments/{attachmentId:guid}", async (Guid mailId, Guid attachmentId, ICurrentMailAccount current, AppDbContext db, LocalAttachmentStorage storage, CancellationToken ct) =>
{
    var attachment = await db.Attachments.SingleOrDefaultAsync(x => x.Id == attachmentId && x.MailId == mailId && x.MailAccountId == current.MailAccountId, ct);
    return attachment is null ? Results.NotFound() : Results.File(await storage.OpenReadAsync(attachment.StoragePath, ct), attachment.ContentType, attachment.FileName);
}).WithName("DownloadAttachment").WithSummary("Download account-owned attachment").Produces(200).Produces(404);
api.MapPost("/mails/send", async (SendRequest request, ICurrentMailAccount current, MailOperationsService service, CancellationToken ct) => Results.Accepted(value: await service.ClaimSendAsync(current.MailAccountId, new(request.To, request.Subject, request.BodyText, request.BodyHtml, request.IdempotencyKey), ct))).WithName("SendMail").WithSummary("Send mail idempotently").Produces<SendOperation>(202).ProducesProblem(409);
api.MapPost("/devices", async (DeviceRequest request, ICurrentMailAccount current, AppDbContext db, CancellationToken ct) => { var token = new DeviceToken { Id = Guid.NewGuid(), MailAccountId = current.MailAccountId, Token = request.Token, Platform = request.Platform, RegisteredAt = DateTime.UtcNow }; db.DeviceTokens.Add(token); await db.SaveChangesAsync(ct); return Results.Created($"/api/devices/{token.Id}", token); }).WithName("RegisterDevice").WithSummary("Register device for current mailbox").Produces<DeviceToken>(201);
api.MapDelete("/devices/{id:guid}", async (Guid id, ICurrentMailAccount current, AppDbContext db, CancellationToken ct) => { var token = await db.DeviceTokens.SingleOrDefaultAsync(x => x.Id == id && x.MailAccountId == current.MailAccountId, ct); if (token is null) return Results.NotFound(); db.Remove(token); await db.SaveChangesAsync(ct); return Results.NoContent(); }).WithName("DeleteDevice").WithSummary("Remove account-owned device").Produces(204).Produces(404);
app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).WithName("Health").WithSummary("Check API health");
app.Run();

public sealed record ReadRequest(bool IsRead);
public sealed record SendRequest(string To, string Subject, string? BodyText, string? BodyHtml, string IdempotencyKey);
public sealed record DeviceRequest(string Token, string Platform);
public partial class Program;
