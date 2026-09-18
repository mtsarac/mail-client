using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using MailClient.Api;
using MailClient.Api.Auth;
using MailClient.Api.Endpoints;
using MailClient.Api.Observability;
using MailClient.Application;
using MailClient.Application.Authentication;
using MailClient.Application.Accounts;
using MailClient.Application.Discovery;
using MailClient.Application.Mail;
using MailClient.Application.Observability;
using MailClient.Application.Runtime;
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
using MailClient.Infrastructure.OAuth;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Push;
using MailClient.Infrastructure.Runtime;
using MailClient.Infrastructure.Security;
using MailClient.Infrastructure.Services;
using MailClient.Infrastructure.Storage;
using MailClient.Infrastructure.Sync;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Amazon.S3;
using Microsoft.OpenApi;
using Serilog;
using Serilog.Formatting.Json;

var builder = WebApplication.CreateBuilder(args);
var observabilityOptions = builder.Configuration.GetSection("Observability").Get<ObservabilityOptions>() ?? new ObservabilityOptions();
observabilityOptions.Validate();
var logFiles = observabilityOptions.Logs;
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Logger(appLog => appLog
        .Filter.ByExcluding(log => log.Properties.ContainsKey("RequestPath"))
        .WriteTo.File(new JsonFormatter(), "logs/app-.json", rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: logFiles.App.RetainedFileCountLimit,
            fileSizeLimitBytes: logFiles.App.FileSizeLimitBytes,
            rollOnFileSizeLimit: true))
    .WriteTo.Logger(httpLog => httpLog
        .Filter.ByIncludingOnly(log => log.Properties.ContainsKey("RequestPath"))
        .WriteTo.File(new JsonFormatter(), "logs/http-.json", rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: logFiles.Http.RetainedFileCountLimit,
            fileSizeLimitBytes: logFiles.Http.FileSizeLimitBytes,
            rollOnFileSizeLimit: true)));
builder.AddMailClientTelemetry(observabilityOptions);
builder.Services.AddMailClientHealthChecks();
builder.Services.Configure<HttpLoggingOptions>(builder.Configuration.GetSection("HttpLogging"));
builder.Services.AddSingleton<Serilog.ILogger>(_ => Serilog.Log.Logger);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
{
    var correlation = context.HttpContext.RequestServices.GetService<CorrelationContext>();
    if (!string.IsNullOrEmpty(correlation?.CorrelationId))
        context.ProblemDetails.Extensions["correlationId"] = correlation.CorrelationId;
});
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
    options.OperationFilter<IdempotencyKeyOperationFilter>();
});
var isProduction = !builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Test");
StartupConfig.Validate(
    builder.Configuration.GetConnectionString("Default"),
    builder.Configuration["DataProtection:KeyPath"],
    builder.Configuration["DataProtection:CertificatePath"],
    isProduction);
var managementOptions = builder.Configuration.GetSection("Management").Get<ManagementOptions>() ?? new ManagementOptions();
managementOptions.Validate(isProduction);
builder.Services.AddSingleton(managementOptions);
builder.Services.AddScoped<ManagementApiKeyFilter>();
var connection = builder.Configuration.GetConnectionString("Default") ?? "Host=localhost;Port=5432;Database=mailclient_v2;Username=postgres;Password=postgres";
builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connection));
var keyPath = builder.Configuration["DataProtection:KeyPath"] ?? Path.Combine(builder.Environment.ContentRootPath, "data", "protection-keys");
var dataProtection = builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keyPath));
var certificatePath = builder.Configuration["DataProtection:CertificatePath"];
if (!string.IsNullOrWhiteSpace(certificatePath))
    dataProtection.ProtectKeysWithCertificate(X509CertificateLoader.LoadCertificateFromFile(certificatePath));
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CorrelationContext>();
builder.Services.AddSingleton<IDnsResolver, SystemDnsResolver>();
builder.Services.AddSingleton(sp => new OutboundHostValidator(sp.GetRequiredService<IDnsResolver>(), allowPrivateHosts: builder.Environment.IsDevelopment()));
builder.Services.AddSingleton<DiscoveryStateStore>();
builder.Services.AddSingleton(new DnsClient.LookupClient());
var mailDiscoveryOptions = builder.Configuration.GetSection("MailDiscovery").Get<MailDiscoveryOptions>() ?? new MailDiscoveryOptions();
mailDiscoveryOptions.Validate();
builder.Services.AddSingleton(mailDiscoveryOptions);
builder.Services.Configure<MailDiscoveryOptions>(builder.Configuration.GetSection("MailDiscovery"));
builder.Services.AddHttpClient<AutoconfigDiscoveryStrategy>()
    .ConfigurePrimaryHttpMessageHandler(sp => new SsrfSafeDiscoveryHttpHandler(sp.GetRequiredService<OutboundHostValidator>()));
builder.Services.AddHttpClient<MicrosoftAutodiscoverStrategy>()
    .ConfigurePrimaryHttpMessageHandler(sp => new SsrfSafeDiscoveryHttpHandler(sp.GetRequiredService<OutboundHostValidator>()));
builder.Services.AddSingleton<IMailDiscoveryStrategy, KnownProviderStrategy>();
builder.Services.AddSingleton<IMailDiscoveryStrategy, DnsSrvDiscoveryStrategy>();
builder.Services.AddTransient<IMailDiscoveryStrategy>(sp => sp.GetRequiredService<AutoconfigDiscoveryStrategy>());
builder.Services.AddTransient<IMailDiscoveryStrategy>(sp => sp.GetRequiredService<MicrosoftAutodiscoverStrategy>());
builder.Services.AddSingleton<IMailDiscoveryStrategy, HeuristicDiscoveryStrategy>();
builder.Services.AddScoped<MailServerDiscoveryService>();
var sessionOptions = builder.Configuration.GetSection("Session").Get<MailClient.Application.Authentication.SessionOptions>() ?? new MailClient.Application.Authentication.SessionOptions();
sessionOptions.Validate();
builder.Services.AddSingleton(sessionOptions);
builder.Services.AddSingleton<MailConnectionHelper>();
builder.Services.AddScoped<MailKitFolderExplorer>();
builder.Services.AddScoped<MailCredentialResolver>();
var oauthOptions = builder.Configuration.GetSection("OAuth").Get<OAuthOptions>() ?? new OAuthOptions();
builder.Services.AddSingleton(oauthOptions);
builder.Services.AddScoped<IRuntimeSettingsStore, RuntimeSettingsStore>();
builder.Services.AddScoped<IRuntimePolicyProvider, RuntimePolicyProvider>();
builder.Services.AddScoped<RuntimeOperationSettings>();
builder.Services.AddScoped<IOAuthStateNonceStore, OAuthStateNonceStore>();
builder.Services.AddHttpClient("OAuth");
builder.Services.AddSingleton<IOAuthProvider>(sp => new GoogleOAuthProvider(sp.GetRequiredService<IHttpClientFactory>().CreateClient("OAuth"), oauthOptions.Google));
builder.Services.AddSingleton<IOAuthProvider>(sp => new MicrosoftOAuthProvider(sp.GetRequiredService<IHttpClientFactory>().CreateClient("OAuth"), oauthOptions.Microsoft));
builder.Services.AddScoped<OAuthStateProtector>(sp => new OAuthStateProtector(sp.GetRequiredService<IDataProtectionProvider>(), TimeSpan.FromMinutes(oauthOptions.StateLifetimeMinutes), sp.GetRequiredService<IOAuthStateNonceStore>()));
builder.Services.AddScoped<OAuthConnectionService>();
builder.Services.AddSingleton<IMailConnectionValidator, MailKitConnectionValidator>();
builder.Services.AddSingleton<IMailServerCandidateValidator>(sp => (MailKitConnectionValidator)sp.GetRequiredService<IMailConnectionValidator>());
builder.Services.AddScoped<ICredentialProtector, DataProtectionCredentialProtector>();
builder.Services.AddScoped<MailSessionService>();
builder.Services.AddScoped<AccountConnectionService>();
builder.Services.AddScoped<IMailTransport, MailKitMailTransport>();
builder.Services.AddScoped<IMailFolderClient, MailFolderClient>();
builder.Services.AddScoped<MailFolderSyncService>();
builder.Services.AddScoped<ISyncExecutor>(sp => sp.GetRequiredService<MailFolderSyncService>());
builder.Services.AddScoped<MailReadService>();
builder.Services.AddScoped<MailSearchService>();
builder.Services.AddScoped<MailQueryService>();

builder.Services.AddScoped<SendOperationStore>();
builder.Services.AddScoped<MailSendService>();
builder.Services.AddScoped<DraftService>();
builder.Services.AddScoped<ComposeContextService>();
builder.Services.AddScoped<MailFolderAccessService>();
builder.Services.AddScoped<IMailOperationService, MailOperationService>();
builder.Services.AddScoped<MailReconciliationService>();
builder.Services.AddScoped<ConversationService>();
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

builder.Services.AddSingleton<ISyncClock, SystemSyncClock>();
builder.Services.AddSingleton<ISyncConnectionBudget, SyncConnectionBudget>();
builder.Services.AddSingleton(new SyncScheduleQueue(1000));
builder.Services.AddSingleton<SyncCoordinator>();
builder.Services.AddSingleton<ISyncScheduler>(sp => sp.GetRequiredService<SyncCoordinator>());
if (!builder.Environment.IsEnvironment("Test"))
    builder.Services.AddHostedService(sp => sp.GetRequiredService<SyncCoordinator>());
builder.Services.AddScoped<ISyncLockProvider>(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var connection = configuration.GetConnectionString("Default");
    var logger = sp.GetRequiredService<ILogger<PostgresSyncLockProvider>>();
    var metrics = sp.GetRequiredService<MailClientMetrics>();
    return string.IsNullOrWhiteSpace(connection)
        ? new InMemorySyncLockProvider(metrics)
        : new PostgresSyncLockProvider(connection, logger, metrics);
});
if (!builder.Environment.IsEnvironment("Test"))
    builder.Services.AddHostedService<MailSyncService>();
var storageOptions = builder.Configuration.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();
storageOptions.Validate();
builder.Services.AddSingleton(storageOptions);
if (storageOptions.Provider == StorageProvider.S3)
{
    builder.Services.AddSingleton<IAmazonS3>(_ => S3StorageFactory.Create(storageOptions.S3));
    builder.Services.AddSingleton<IFileStorage>(sp => new S3AttachmentStorage(sp.GetRequiredService<IAmazonS3>(), storageOptions));
}
else
{
    builder.Services.AddSingleton(new LocalAttachmentStorage(Path.Combine(builder.Environment.ContentRootPath, "data")));
    builder.Services.AddSingleton<IFileStorage>(sp => sp.GetRequiredService<LocalAttachmentStorage>());
}
var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new("MailClient", "MailClient", "development-only-key-change-before-production-123456789", 15);
if (jwt.Key.Length < 32) throw new InvalidOperationException("Jwt:Key must contain at least 32 characters.");
if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Test") && jwt.Key == JwtOptions.DevelopmentKey)
    throw new InvalidOperationException("Jwt:Key must be provided via configuration in production; the development key is not allowed.");
builder.Services.AddSingleton(jwt);
builder.Services.AddSingleton<IJwtTokenIssuer, JwtTokenIssuer>();
builder.Services.AddScoped<ICurrentMailAccount, CurrentMailAccount>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.MapInboundClaims = false;
    options.TokenValidationParameters = new()
    {
        ValidateIssuer = true,
        ValidIssuer = jwt.Issuer,
        ValidateAudience = true,
        ValidAudience = jwt.Audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
        ValidateLifetime = true,
        NameClaimType = JwtRegisteredClaimNames.Sub
    };
    options.Events = new JwtBearerEvents
    {
        OnTokenValidated = async context =>
        {
            if (!MailAccountClaims.TryGetAccountId(context.Principal, out var mailAccountId))
            {
                context.Fail("JWT subject is not a mail account id.");
                return;
            }

            var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
            var exists = await db.MailAccounts
                .AsNoTracking()
                .AnyAsync(account => account.Id == mailAccountId, context.HttpContext.RequestAborted);
            if (!exists)
                context.Fail("Mail account no longer exists.");
        }
    };
});
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context => RateLimitPartition.GetFixedWindowLimiter(
        MailAccountClaims.GetAccountIdOrNull(context.User) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) }));
});
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddCors(options => options.AddPolicy("DevelopmentLan", policy => policy
        .AllowAnyOrigin()
        .AllowAnyHeader()
        .AllowAnyMethod()));
}

var knownProxies = builder.Configuration.GetSection("Proxy:KnownProxies").Get<string[]>() ?? [];
var knownNetworks = builder.Configuration.GetSection("Proxy:KnownNetworks").Get<string[]>() ?? [];
var trustedProxy = knownProxies.Length > 0 || knownNetworks.Length > 0;
if (trustedProxy)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var proxy in knownProxies)
            if (IPAddress.TryParse(proxy, out var address))
                options.KnownProxies.Add(address);
        foreach (var network in knownNetworks)
        {
            var parts = network.Split('/');
            if (parts.Length == 2 && IPAddress.TryParse(parts[0], out var prefix) && int.TryParse(parts[1], out var prefixLength))
                options.KnownIPNetworks.Add(new System.Net.IPNetwork(prefix, prefixLength));
        }
    });
}

var app = builder.Build();
app.UseMiddleware<CorrelationMiddleware>();
app.UseMiddleware<HttpBodyLoggingMiddleware>();
app.UseExceptionHandler(error => error.Run(async context =>
{
    var exception = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    var (code, status) = ApiFailureMapper.MapFailure(exception);
    if (status >= 500)
        app.Logger.LogError(exception, "Unhandled request failure {Code} for {Method} {Path}.", code, context.Request.Method, context.Request.Path);
    var correlationId = context.RequestServices.GetRequiredService<CorrelationContext>().CorrelationId;
    var extensions = new Dictionary<string, object?> { ["code"] = code, ["correlationId"] = correlationId };
    if (app.Environment.IsDevelopment() && exception is not null)
        extensions["detail"] = $"{exception.GetType().Name}: {exception.Message}";
    context.Response.StatusCode = status;
    await Results.Problem(title: code.Replace('_', ' '), statusCode: status, extensions: extensions).ExecuteAsync(context);
}));
if (trustedProxy)
    app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Test"))
    app.UseHttpsRedirection();
if (app.Environment.IsDevelopment())
    app.UseCors("DevelopmentLan");
app.UseAuthentication();
app.UseMiddleware<AuthenticatedLogEnrichmentMiddleware>();
app.UseRateLimiter();
app.UseAuthorization();
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(options => options.SwaggerEndpoint("/swagger/v2/swagger.json", "Mail Client v2")); }

app.MapAccountEndpoints();
app.MapOAuthEndpoints();
app.MapAuthEndpoints();
app.MapFolderEndpoints();
app.MapMailEndpoints();
app.MapConversationEndpoints();
app.MapDeviceEndpoints();
app.MapManagementEndpoints();
app.MapHealthEndpoints();
app.MapMailClientTelemetry(observabilityOptions);

app.Run();

public partial class Program;
