using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using MailClient.Api.Health;
using MailClient.Api.Auth;
using MailClient.Api.Accounts;
using MailClient.Api.Mails;
using MailClient.Application.Interfaces;
using MailClient.Application.Sync;
using MailClient.Infrastructure.Email;
using MailClient.Infrastructure.Identity;
using MailClient.Infrastructure.Network;
using MailClient.Infrastructure.Persistence;
using MailClient.Infrastructure.Security;
using MailClient.Infrastructure.Services;
using MailClient.Infrastructure.Storage;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);
if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
    builder.Configuration.AddEnvironmentVariables();
}

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20,
            Window = TimeSpan.FromMinutes(1)
        }));
    options.AddPolicy("mail-operations", context =>
    {
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? context.User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        var key = $"mail:{userId ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20,
            Window = TimeSpan.FromMinutes(1)
        });
    });
});
builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition("bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Enter the JWT access token. Example: 'Bearer {token}'"
    });
    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("bearer", document)] = []
    });
});
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
var mailSyncOptions = builder.Configuration.GetSection("MailSync").Get<MailSyncOptions>() ?? new MailSyncOptions();
mailSyncOptions.Validate();
builder.Services.AddSingleton(mailSyncOptions);
var attachmentRoot = builder.Configuration["MailSync:AttachmentRoot"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "data");
var configuredKeyPath = builder.Configuration["DataProtection:KeyPath"];
var keyPath = configuredKeyPath is null
    ? Path.Combine(builder.Environment.ContentRootPath, "data", "protection-keys")
    : Path.IsPathFullyQualified(configuredKeyPath)
        ? configuredKeyPath
        : Path.Combine(builder.Environment.ContentRootPath, configuredKeyPath);
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keyPath));
var trustedProxy = (builder.Configuration.GetSection("Proxy:KnownProxies").Get<string[]>() ?? []).Length > 0
    || (builder.Configuration.GetSection("Proxy:KnownNetworks").Get<string[]>() ?? []).Length > 0;
if (trustedProxy)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var proxy in builder.Configuration.GetSection("Proxy:KnownProxies").Get<string[]>() ?? [])
            if (IPAddress.TryParse(proxy, out var address))
                options.KnownProxies.Add(address);
        foreach (var network in builder.Configuration.GetSection("Proxy:KnownNetworks").Get<string[]>() ?? [])
        {
            var parts = network.Split('/');
            if (parts.Length == 2
                && IPAddress.TryParse(parts[0], out var prefix)
                && int.TryParse(parts[1], out var prefixLength))
                options.KnownIPNetworks.Add(new System.Net.IPNetwork(prefix, prefixLength));
        }
    });
}
builder.Services.AddSingleton<IDnsResolver, SystemDnsResolver>();
builder.Services.AddSingleton<IOutboundHostValidator, OutboundHostValidator>();
builder.Services.AddScoped<MailConnectionHelper>();
builder.Services.AddScoped<IMailConnectivityTester, MailKitConnectivityTester>();
builder.Services.AddScoped<IMailFolderExplorer, MailKitFolderExplorer>();
builder.Services.AddScoped<ICredentialProtector, DataProtectionCredentialProtector>();
builder.Services.AddScoped<IMailAccountService, MailAccountService>();
builder.Services.AddScoped<IMailFolderService, MailFolderService>();
builder.Services.AddScoped<IMailReadService, MailReadService>();
builder.Services.AddScoped<IMailQueryService, MailQueryService>();
builder.Services.AddScoped<IMailSendService, MailSendService>();
builder.Services.AddScoped<SendOperationStore>();
builder.Services.AddScoped<IMailTransport, MailKitMailTransport>();
builder.Services.AddScoped<IFileStorage>(_ => new LocalFileStorage(attachmentRoot));
builder.Services.AddScoped<MailFolderSyncService>();
builder.Services.AddScoped<IMailFolderClient, MailFolderClient>();
if (mailSyncOptions.Enabled)
    builder.Services.AddHostedService<MailSyncService>();
builder.Services.AddScoped<IAuthenticationService, AuthenticationService>();
builder.Services.AddScoped<IUserAdministrationService, UserAdministrationService>();
builder.Services.AddScoped<IUserSessionValidator, UserSessionValidator>();
builder.Services.AddScoped<IHealthProbe, DatabaseHealthProbe>();
builder.Services.AddSingleton<IJwtTokenIssuer, JwtTokenIssuer>();
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
builder.Services.AddScoped<IPasswordHasher<MailClient.Domain.Entities.User>, PasswordHasher<MailClient.Domain.Entities.User>>();
var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
if (jwt.Key.Length < 32)
    throw new InvalidOperationException("Jwt:Key must be configured with at least 32 characters.");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
            ValidateLifetime = true
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var validator = context.HttpContext.RequestServices.GetRequiredService<IUserSessionValidator>();
                var principal = context.Principal;
                var userId = principal?.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? principal?.FindFirstValue(JwtRegisteredClaimNames.Sub);
                var version = principal?.FindFirst(JwtTokenIssuer.TokenVersionClaim)?.Value;
                if (!Guid.TryParse(userId, out var id) || !int.TryParse(version, out var tokenVersion))
                {
                    context.Fail("Session is invalid.");
                    return;
                }

                var session = await validator.ValidateAsync(id, tokenVersion, context.HttpContext.RequestAborted);
                if (session is null)
                {
                    context.Fail("Session is no longer valid.");
                    return;
                }

                if (principal?.Identity is ClaimsIdentity identity)
                {
                    foreach (var claim in identity.FindAll(ClaimTypes.Role).ToList())
                        identity.RemoveClaim(claim);
                    identity.AddClaim(new Claim(ClaimTypes.Role, session.Role.ToString()));
                }
            }
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Test"))
{
    app.Logger.LogWarning(
        "DataProtection keys persist at {KeyPath}. Production requires a persistent shared volume, restrictive file permissions, and key protection at rest; all instances must share the same ring.",
        keyPath);
}

if (trustedProxy)
    app.UseForwardedHeaders();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseExceptionHandler();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapHealthEndpoints();
app.MapAuthEndpoints();
app.MapAdminUserEndpoints();
app.MapMailAccountEndpoints();
app.MapMailEndpoints();

app.Run();

public partial class Program;
