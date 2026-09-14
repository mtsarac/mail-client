using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using MailClient.Api.Auth;
using MailClient.Api.Observability;
using MailClient.Application.Accounts;
using MailClient.Application.Discovery;
using MailClient.Application.Mail;
using MailClient.Application.Sync;
using MailClient.Domain;
using MailClient.Domain.Entities;
using MailClient.Domain.Enums;
using MailClient.Infrastructure.Accounts;
using MailClient.Infrastructure.Authentication;
using MailClient.Infrastructure.Discovery;
using MailClient.Infrastructure.Mail;
using MailClient.Infrastructure.Network;
using MailClient.Infrastructure.Observability;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Push;
using MailClient.Infrastructure.Security;
using MailClient.Infrastructure.Services;
using MailClient.Infrastructure.Storage;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Serilog;
using Serilog.Formatting.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Logger(appLog => appLog
        .Filter.ByExcluding(log => log.Properties.ContainsKey("RequestPath"))
        .WriteTo.File(new JsonFormatter(), "logs/app-.json", rollingInterval: RollingInterval.Day))
    .WriteTo.Logger(httpLog => httpLog
        .Filter.ByIncludingOnly(log => log.Properties.ContainsKey("RequestPath"))
        .WriteTo.File(new JsonFormatter(), "logs/http-.json", rollingInterval: RollingInterval.Day)));
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v2", new OpenApiInfo
    {
        Title = "Mail Client API",
        Version = "v2",
        Description = """
            MailAccount-principal API. Automatic discovery is preferred; manual setup remains SSRF/TLS/auth validated. Credentials are never returned.
            Discovery failure example (HTTP 422): { "title": "Mail server discovery failed.", "status": 422, "code": "mail_discovery_failed", "manualSetupAvailable": true }.
            Manual connect example: { "email": "person@example.com", "username": "person@example.com", "authentication": { "type": "Password", "password": "ExamplePassword123!" }, "imap": { "host": "imap.example.com", "port": 993, "security": "SslOnConnect" }, "smtp": { "host": "smtp.example.com", "port": 465, "security": "SslOnConnect" } }.
            """
    });
    options.AddSecurityDefinition("bearer", new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT" });
});
var connection = builder.Configuration.GetConnectionString("Default") ?? "Host=localhost;Port=5432;Database=mailclient_v2;Username=postgres;Password=postgres";
builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connection));
var keyPath = builder.Configuration["DataProtection:KeyPath"] ?? Path.Combine(builder.Environment.ContentRootPath, "data", "protection-keys");
var dataProtection = builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keyPath));
var certificatePath = builder.Configuration["DataProtection:CertificatePath"];
if (!string.IsNullOrWhiteSpace(certificatePath))
    dataProtection.ProtectKeysWithCertificate(X509CertificateLoader.LoadCertificateFromFile(certificatePath));
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
var mailSyncOptions = builder.Configuration.GetSection("MailSync").Get<MailSyncOptions>() ?? new MailSyncOptions();
mailSyncOptions.Validate();
builder.Services.AddSingleton(mailSyncOptions);
builder.Services.AddSingleton<MailConnectionHelper>();
builder.Services.AddSingleton<MailKitConnectivityTester>();
builder.Services.AddScoped<MailKitFolderExplorer>();
builder.Services.AddScoped<MailCredentialResolver>();
builder.Services.AddSingleton<IMailConnectionValidator, MailKitConnectionValidator>();
builder.Services.AddSingleton<IMailServerCandidateValidator>(sp => (MailKitConnectionValidator)sp.GetRequiredService<IMailConnectionValidator>());
builder.Services.AddScoped<ICredentialProtector, DataProtectionCredentialProtector>();
builder.Services.AddScoped<MailSessionService>();
builder.Services.AddScoped<AccountConnectionService>();
builder.Services.AddScoped<IMailTransport, MailKitMailTransport>();
builder.Services.AddScoped<IMailFolderClient, MailFolderClient>();
builder.Services.AddScoped<MailFolderSyncService>();
builder.Services.AddScoped<MailReadService>();
builder.Services.AddScoped<SendOperationStore>();
builder.Services.AddScoped<MailSendService>();
builder.Services.AddScoped<MailOperationsService>();
builder.Services.AddScoped<AuditLogger>();
var firebaseOptions = builder.Configuration.GetSection("Firebase").Get<FirebaseOptions>() ?? new FirebaseOptions();
firebaseOptions.Validate();
builder.Services.AddSingleton(firebaseOptions);
if (firebaseOptions.Enabled)
{
    builder.Services.AddSingleton<IFirebaseMessageSender, FirebaseMessageSender>();
    builder.Services.AddSingleton<IFirebaseGateway, FirebaseAdminGateway>();
    builder.Services.AddScoped<IPushNotificationService, FirebasePushNotificationService>();
}
else
{
    builder.Services.AddScoped<IPushNotificationService, NoOpPushNotificationService>();
}

builder.Services.AddSingleton<InitialSyncQueue>();
builder.Services.AddHostedService<InitialSyncWorker>();
if (mailSyncOptions.Enabled)
    builder.Services.AddHostedService<MailSyncService>();
builder.Services.AddSingleton(new LocalAttachmentStorage(Path.Combine(builder.Environment.ContentRootPath, "data")));
builder.Services.AddSingleton<IFileStorage>(sp => sp.GetRequiredService<LocalAttachmentStorage>());
var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new("MailClient", "MailClient", "development-only-key-change-before-production-123456789", 15);
if (jwt.Key.Length < 32) throw new InvalidOperationException("Jwt:Key must contain at least 32 characters.");
builder.Services.AddSingleton(jwt);
builder.Services.AddSingleton<IJwtTokenIssuer, JwtTokenIssuer>();
builder.Services.AddScoped<ICurrentMailAccount, CurrentMailAccount>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options => options.TokenValidationParameters = new()
{
    ValidateIssuer = true,
    ValidIssuer = jwt.Issuer,
    ValidateAudience = true,
    ValidAudience = jwt.Audience,
    ValidateIssuerSigningKey = true,
    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
    ValidateLifetime = true,
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
    context.Response.StatusCode = code switch
    {
        "mail_authentication_failed" => 401,
        "invalid_recipient" or "body_required" or "body_too_large" or "too_many_attachments"
            or "attachment_too_large" or "message_not_constructible" or "idempotency_key_required"
            or "idempotency_key_too_long" or "invalid_mail_header" => 400,
        "mail_server_unsafe" or "unsupported_authentication_method" or "invalid_email"
            or "oauth_not_implemented" => 422,
        "mail_account_not_found" => 404,
        "idempotency_conflict" or "send_in_progress" or "delivery_unknown" or "mailbox_changed"
            or "credential_missing" or "mail_account_needs_reauthentication" => 409,
        "mail_account_disabled" => 403,
        _ => 500
    };
    await Results.Problem(title: code.Replace('_', ' '), statusCode: context.Response.StatusCode, extensions: new Dictionary<string, object?> { ["code"] = code, ["correlationId"] = context.TraceIdentifier }).ExecuteAsync(context);
}));
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<CorrelationMiddleware>();
app.UseSerilogRequestLogging();
app.UseAuthorization();
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(options => options.SwaggerEndpoint("/swagger/v2/swagger.json", "Mail Client v2")); }

var accounts = app.MapGroup("/api/accounts").WithTags("Accounts");
accounts.MapPost("/discover", async (DiscoverRequest request, MailServerDiscoveryService discovery, DiscoveryStateStore states, AuditLogger audit, HttpContext http, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@')) return Results.ValidationProblem(new Dictionary<string, string[]> { ["email"] = ["Valid email is required."] });
    var candidate = await discovery.DiscoverAsync(request.Email.Trim(), ct);
    if (candidate is null)
    {
        await audit.WriteAsync(null, AuditActions.MailAccountDiscoveryFailed, "MailAccount", null,
            new Dictionary<string, string?> { ["email"] = request.Email.Trim() }, http.TraceIdentifier, ct);
        return Results.Problem(title: "Mail server discovery failed.", statusCode: 422, extensions: new Dictionary<string, object?> { ["code"] = "mail_discovery_failed", ["manualSetupAvailable"] = true });
    }

    var id = states.Store(request.Email.Trim(), candidate, TimeSpan.FromMinutes(10));
    await audit.WriteAsync(null, AuditActions.MailAccountDiscoverySucceeded, "MailAccount", null,
        new Dictionary<string, string?> { ["email"] = request.Email.Trim(), ["provider"] = candidate.Provider.ToString(), ["source"] = candidate.Source.ToString() }, http.TraceIdentifier, ct);
    return Results.Ok(new DiscoverResponse(id, request.Email.Trim(), candidate.Provider, candidate.AuthenticationMethods, true));
}).AllowAnonymous().WithName("DiscoverMailAccount").WithSummary("Discover mail servers").WithDescription("Runs known provider, DNS SRV, autoconfig, Autodiscover, then safe heuristics. Manual setup is fallback only.").Produces<DiscoverResponse>().ProducesProblem(422).ProducesValidationProblem();
accounts.MapPost("/connect", async (ConnectRequest request, DiscoveryStateStore states, AccountConnectionService service, AuditLogger audit, HttpContext http, CancellationToken ct) =>
{
    if (states.Take(request.DiscoveryId) is not { } state)
        return Results.Problem(statusCode: 422, extensions: new Dictionary<string, object?> { ["code"] = "discovery_expired" });
    try
    {
        var tokens = await service.ConnectAsync(state, request.Authentication, request.DeviceIdentifier, ct);
        await audit.WriteAsync(tokens.MailAccountId, AuditActions.MailAccountConnected, "MailAccount", tokens.MailAccountId.ToString(),
            new Dictionary<string, string?> { ["provider"] = state.Candidate.Provider.ToString() }, http.TraceIdentifier, ct);
        return Results.Ok(tokens);
    }
    catch (InvalidOperationException ex) when (ex.Message == "mail_authentication_failed")
    {
        await audit.WriteAsync(null, AuditActions.MailAccountAuthenticationFailed, "MailAccount", null,
            new Dictionary<string, string?> { ["email"] = state.Email }, http.TraceIdentifier, ct);
        throw;
    }
}).AllowAnonymous().WithName("ConnectDiscoveredAccount").WithSummary("Connect discovered mailbox").Produces<TokenResponse>().ProducesProblem(422);
accounts.MapPost("/connect-manual", async (ManualConnectRequest request, AccountConnectionService service, AuditLogger audit, HttpContext http, CancellationToken ct) =>
{
    await audit.WriteAsync(null, AuditActions.MailAccountManualSetupAttempted, "MailAccount", null,
        new Dictionary<string, string?> { ["email"] = request.Email, ["imapHost"] = request.Imap.Host, ["smtpHost"] = request.Smtp.Host }, http.TraceIdentifier, ct);
    var tokens = await service.ConnectManualAsync(request, ct);
    await audit.WriteAsync(tokens.MailAccountId, AuditActions.MailAccountManualSetupSucceeded, "MailAccount", tokens.MailAccountId.ToString(), null, http.TraceIdentifier, ct);
    return Results.Ok(tokens);
}).AllowAnonymous().WithName("ConnectManualAccount").WithSummary("Connect mailbox with manual server settings").WithDescription("Fallback only. Host, IP, TLS, IMAP, SMTP, and credentials receive the same validation as automatic discovery.").Produces<TokenResponse>().ProducesProblem(422);

var auth = app.MapGroup("/api/auth").WithTags("Authentication");
auth.MapPost("/refresh", async (RefreshRequest request, MailSessionService sessions, IJwtTokenIssuer tokens, AuditLogger audit, HttpContext http, CancellationToken ct) =>
{
    var rotation = await sessions.RotateAsync(request.RefreshToken, TimeSpan.FromDays(30), ct);
    if (rotation is null) return Results.Problem(statusCode: 401, extensions: new Dictionary<string, object?> { ["code"] = "invalid_refresh_token" });
    var access = tokens.Issue(rotation.Value.Session.MailAccountId);
    await audit.WriteAsync(rotation.Value.Session.MailAccountId, AuditActions.MailSessionRefreshed, "MailSession", rotation.Value.Session.Id.ToString(), null, http.TraceIdentifier, ct);
    return Results.Ok(new TokenResponse(access.Token, rotation.Value.Token, rotation.Value.Session.MailAccountId, access.ExpiresAt));
}).AllowAnonymous().WithName("RefreshSession").WithSummary("Rotate refresh session").Produces<TokenResponse>().ProducesProblem(401);
auth.MapPost("/logout", async (LogoutRequest request, MailSessionService sessions, AuditLogger audit, HttpContext http, CancellationToken ct) =>
{
    if (!await sessions.RevokeAsync(request.RefreshToken, ct))
        return Results.Problem(statusCode: 401, extensions: new Dictionary<string, object?> { ["code"] = "session_revoked" });
    await audit.WriteAsync(null, AuditActions.MailSessionRevoked, "MailSession", null, null, http.TraceIdentifier, ct);
    return Results.NoContent();
}).AllowAnonymous().WithName("LogoutSession").WithSummary("Revoke client session").Produces(204).ProducesProblem(401);

var api = app.MapGroup("/api").RequireAuthorization();
api.MapGet("/account", async (ICurrentMailAccount current, AppDbContext db, CancellationToken ct) => await db.MailAccounts.Where(x => x.Id == current.MailAccountId).Select(x => new AccountResponse(x.Id, x.EmailAddress, x.DisplayName, x.Provider, x.Status)).SingleOrDefaultAsync(ct) is { } account ? Results.Ok(account) : Results.NotFound()).WithName("GetCurrentAccount").WithSummary("Get current mailbox account").Produces<AccountResponse>().Produces(404);
api.MapDelete("/account", async (ICurrentMailAccount current, AppDbContext db, LocalAttachmentStorage storage, CancellationToken ct) =>
{
    var account = await db.MailAccounts.FindAsync([current.MailAccountId], ct);
    if (account is null) return Results.NotFound();
    db.Remove(account);
    await db.SaveChangesAsync(ct);
    await storage.DeleteAccountAsync(current.MailAccountId, CancellationToken.None);
    return Results.NoContent();
}).WithName("DeleteCurrentAccount").WithSummary("Delete mailbox and cached data").Produces(204).Produces(404);
api.MapGet("/folders", async (ICurrentMailAccount current, AppDbContext db, CancellationToken ct) => Results.Ok(await db.MailFolders.Where(x => x.MailAccountId == current.MailAccountId).ToListAsync(ct))).WithName("ListFolders").WithSummary("List mailbox folders").Produces<List<MailFolder>>();
api.MapPost("/folders/refresh", async (ICurrentMailAccount current, AccountConnectionService connector, AuditLogger audit, HttpContext http, CancellationToken ct) =>
{
    var count = await connector.RefreshFoldersAsync(current.MailAccountId, ct);
    await audit.WriteAsync(current.MailAccountId, AuditActions.MailSyncRequested, "MailFolder", null,
        new Dictionary<string, string?> { ["folders"] = count.ToString() }, http.TraceIdentifier, ct);
    return Results.Accepted(value: new { folders = count });
}).WithName("RefreshFolders").WithSummary("Refresh mailbox folders").Produces(202).ProducesProblem(409).ProducesProblem(502);
api.MapPost("/folders/{id:guid}/sync", async (Guid id, ICurrentMailAccount current, MailOperationsService service, InitialSyncQueue queue, AuditLogger audit, HttpContext http, CancellationToken ct) =>
{
    if (!await service.SyncFolderAsync(current.MailAccountId, id, ct)) return Results.NotFound();
    await queue.EnqueueAsync(current.MailAccountId, ct);
    await audit.WriteAsync(current.MailAccountId, AuditActions.MailSyncRequested, "MailFolder", id.ToString(), null, http.TraceIdentifier, ct);
    return Results.Accepted();
}).WithName("SyncFolder").WithSummary("Request folder synchronization").Produces(202).Produces(404);
api.MapGet("/mails", async (ICurrentMailAccount current, AppDbContext db, CancellationToken ct) => Results.Ok(await db.Mails.Where(x => x.MailAccountId == current.MailAccountId).OrderByDescending(x => x.ReceivedAt).Take(100).ToListAsync(ct))).WithName("ListMails").WithSummary("List current mailbox mail").Produces<List<MailClient.Domain.Entities.Mail>>();
api.MapGet("/mails/{id:guid}", async (Guid id, ICurrentMailAccount current, MailReadService reader, CancellationToken ct) => await reader.GetAsync(current.MailAccountId, id, ct) is { } mail ? Results.Ok(mail) : Results.NotFound()).WithName("GetMail").WithSummary("Get mailbox mail").Produces<MailClient.Domain.Entities.Mail>().Produces(404);
api.MapPatch("/mails/{id:guid}/read", async (Guid id, ReadRequest request, ICurrentMailAccount current, AppDbContext db, MailReadService reader, HttpContext http, CancellationToken ct) =>
{
    var status = await db.MailAccounts.Where(x => x.Id == current.MailAccountId).Select(x => x.Status).SingleOrDefaultAsync(ct);
    if (status == MailAccountStatus.NeedsReauthentication)
        return Results.Problem(title: "Mailbox needs reauthentication.", statusCode: 409, extensions: new Dictionary<string, object?> { ["code"] = "mail_account_needs_reauthentication", ["correlationId"] = http.TraceIdentifier });
    var outcome = await reader.SetReadAsync(current.MailAccountId, id, request.IsRead, http.TraceIdentifier, ct);
    if (!outcome.Found) return Results.NotFound();
    if (outcome.Conflict) return Results.Problem(title: "Mailbox folder changed.", statusCode: 409, extensions: new Dictionary<string, object?> { ["code"] = "mailbox_changed", ["correlationId"] = http.TraceIdentifier });
    if (outcome.ProviderError) return Results.Problem(title: "Mail provider unavailable.", statusCode: 502, extensions: new Dictionary<string, object?> { ["code"] = "mail_provider_unavailable", ["correlationId"] = http.TraceIdentifier });
    return Results.NoContent();
}).WithName("SetMailReadState").WithSummary("Change mail read state").Produces(204).Produces(404).ProducesProblem(409).ProducesProblem(502);
api.MapGet("/mails/{mailId:guid}/attachments/{attachmentId:guid}", async (Guid mailId, Guid attachmentId, ICurrentMailAccount current, AppDbContext db, LocalAttachmentStorage storage, CancellationToken ct) =>
{
    var attachment = await db.Attachments.SingleOrDefaultAsync(x => x.Id == attachmentId && x.MailId == mailId && x.MailAccountId == current.MailAccountId, ct);
    return attachment is null ? Results.NotFound() : Results.File(await storage.OpenReadAsync(attachment.StoragePath, ct), attachment.ContentType, attachment.FileName);
}).WithName("DownloadAttachment").WithSummary("Download account-owned attachment").Produces(200).Produces(404);
api.MapPost("/mails/send", async (HttpRequest request, ICurrentMailAccount current, MailSendService sender, CancellationToken ct) =>
{
    var key = request.Headers["Idempotency-Key"].ToString();
    var form = await request.ReadFormAsync(ct);
    static string? Optional(IFormCollection f, string name)
    {
        var value = f[name].ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    var attachments = new List<SendMailAttachment>();
    foreach (var file in form.Files)
        attachments.Add(new SendMailAttachment(file.FileName, file.ContentType, file.OpenReadStream()));
    var command = new SendMailCommand(current.MailAccountId, form["to"].ToString(), form["subject"].ToString(), Optional(form, "bodyHtml"), Optional(form, "bodyText"), attachments)
    {
        IdempotencyKey = key
    };
    var result = await sender.SendAsync(current.MailAccountId, command, request.HttpContext.TraceIdentifier, ct);
    return Results.Ok(new { sent = result.Sent, sentCopySaved = result.SentCopySaved, warning = result.Warning });
}).WithName("SendMail").WithSummary("Send mail idempotently").WithDescription("multipart/form-data: to, subject, bodyHtml and/or bodyText, up to 20 attachments. Idempotency-Key header is required.").Accepts<IFormCollection>("multipart/form-data").Produces(200).ProducesProblem(400).ProducesProblem(409).DisableAntiforgery();
api.MapPost("/devices", async (DeviceRequest request, ICurrentMailAccount current, AppDbContext db, AuditLogger audit, HttpContext http, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Token) || request.Token.Length > 500)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["token"] = ["Device token is required and at most 500 characters."] });
    var now = DateTime.UtcNow;
    var existing = await db.DeviceTokens.SingleOrDefaultAsync(x => x.MailAccountId == current.MailAccountId && x.Token == request.Token, ct);
    if (existing is null)
    {
        existing = new DeviceToken { Id = Guid.NewGuid(), MailAccountId = current.MailAccountId, Token = request.Token, Platform = request.Platform, RegisteredAt = now };
        db.DeviceTokens.Add(existing);
    }
    else
    {
        existing.Platform = request.Platform;
        existing.LastSeenAt = now;
    }

    await db.SaveChangesAsync(ct);
    await audit.WriteAsync(current.MailAccountId, AuditActions.DeviceRegistered, "DeviceToken", existing.Id.ToString(), null, http.TraceIdentifier, ct);
    return Results.Created($"/api/devices/{existing.Id}", existing);
}).WithName("RegisterDevice").WithSummary("Register device for current mailbox").Produces<DeviceToken>(201).ProducesValidationProblem();
api.MapDelete("/devices/{id:guid}", async (Guid id, ICurrentMailAccount current, AppDbContext db, AuditLogger audit, HttpContext http, CancellationToken ct) =>
{
    var token = await db.DeviceTokens.SingleOrDefaultAsync(x => x.Id == id && x.MailAccountId == current.MailAccountId, ct);
    if (token is null) return Results.NotFound();
    db.Remove(token);
    await db.SaveChangesAsync(ct);
    await audit.WriteAsync(current.MailAccountId, AuditActions.DeviceRemoved, "DeviceToken", id.ToString(), null, http.TraceIdentifier, ct);
    return Results.NoContent();
}).WithName("DeleteDevice").WithSummary("Remove account-owned device").Produces(204).Produces(404);
app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).WithName("Health").WithSummary("Check API health").Produces(200);
app.Run();

public sealed record ReadRequest(bool IsRead);
public sealed record DeviceRequest(string Token, string Platform);
public partial class Program;
