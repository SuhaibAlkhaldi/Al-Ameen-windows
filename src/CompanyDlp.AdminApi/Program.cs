using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using CompanyDlp.AdminApi.Configuration;
using CompanyDlp.AdminApi.Data;
using CompanyDlp.AdminApi.Domain;
using CompanyDlp.AdminApi.Endpoints;
using CompanyDlp.AdminApi.Security;
using CompanyDlp.AdminApi.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("COMPANY_DLP_ADMIN_URL") ?? "http://127.0.0.1:5060");

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.Configure<OnboardingOptions>(builder.Configuration.GetSection(OnboardingOptions.SectionName));
builder.Services.Configure<AgentSecurityOptions>(builder.Configuration.GetSection(AgentSecurityOptions.SectionName));
builder.Services.Configure<PolicyDeliveryOptions>(builder.Configuration.GetSection(PolicyDeliveryOptions.SectionName));
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection(DatabaseOptions.SectionName));

var connectionString = builder.Configuration.GetConnectionString("CompanyDlp")
    ?? throw new InvalidOperationException("ConnectionStrings:CompanyDlp is required.");
builder.Services.AddDbContext<CompanyDlpDbContext>(options => options.UseSqlServer(connectionString));

var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
var jwtSigningKey = jwt.SigningKey ?? "";
if (Encoding.UTF8.GetByteCount(jwtSigningKey) < 32 || jwtSigningKey.StartsWith("SET_WITH_", StringComparison.Ordinal)
    || string.IsNullOrWhiteSpace(jwt.Issuer) || string.IsNullOrWhiteSpace(jwt.Audience))
    throw new InvalidOperationException("Jwt issuer, audience, and a secret signing key of at least 32 UTF-8 bytes are required.");
var delivery = builder.Configuration.GetSection(PolicyDeliveryOptions.SectionName).Get<PolicyDeliveryOptions>() ?? new PolicyDeliveryOptions();
var deliveryMode = delivery.Mode ?? "";
if (!deliveryMode.Equals("Development", StringComparison.OrdinalIgnoreCase)
    && !deliveryMode.Equals("Production", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("PolicyDelivery:Mode must be Development or Production.");
if (!Uri.TryCreate(delivery.PublicBaseUrl, UriKind.Absolute, out var publicApiUri)
    || (publicApiUri.Scheme != Uri.UriSchemeHttp && publicApiUri.Scheme != Uri.UriSchemeHttps))
    throw new InvalidOperationException("PolicyDelivery:PublicBaseUrl must be an absolute HTTP or HTTPS URL.");
if (deliveryMode.Equals("Production", StringComparison.OrdinalIgnoreCase))
{
    if (publicApiUri.Scheme != Uri.UriSchemeHttps)
        throw new InvalidOperationException("PolicyDelivery:PublicBaseUrl must use HTTPS in Production.");
    if (string.IsNullOrWhiteSpace(delivery.SigningPrivateKeyPem) && string.IsNullOrWhiteSpace(delivery.SigningPrivateKeyPath))
        throw new InvalidOperationException("A Production ECDSA policy-signing private key is required.");
}

var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSigningKey));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var principal = context.Principal;
                if (!Guid.TryParse(principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var adminId)
                    || !Guid.TryParse(principal?.FindFirstValue("tenant_id"), out var tenantId)
                    || !int.TryParse(principal?.FindFirstValue("token_version"), out var tokenVersion))
                {
                    context.Fail("Administrator token claims are invalid.");
                    return;
                }

                var database = context.HttpContext.RequestServices.GetRequiredService<CompanyDlpDbContext>();
                var account = await database.AdminUsers.AsNoTracking()
                    .Where(value => value.Id == adminId && value.TenantId == tenantId && value.IsActive)
                    .Where(value => database.Tenants.Any(tenant => tenant.Id == tenantId && tenant.IsActive))
                    .Select(value => new { value.Role, value.TokenVersion })
                    .SingleOrDefaultAsync(context.HttpContext.RequestAborted);
                var claimedRole = principal?.FindFirstValue(ClaimTypes.Role);
                if (account is null
                    || account.TokenVersion != tokenVersion
                    || !account.Role.Equals(claimedRole, StringComparison.Ordinal))
                    context.Fail("Administrator account is inactive or the token has been revoked.");
            }
        };
    });
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddPolicy("AdminPortal", policy =>
{
    if (allowedOrigins.Length > 0)
        policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod();
}));

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("OwnerOnly", policy => policy.RequireRole(AdminRoles.Owner));
    options.AddPolicy("PolicyAdmin", policy => policy.RequireRole(AdminRoles.Owner, AdminRoles.PolicyAdmin));
    options.AddPolicy("AuditRead", policy => policy.RequireRole(AdminRoles.Owner, AdminRoles.PolicyAdmin, AdminRoles.Auditor));
});
builder.Services.AddProblemDetails();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("Auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    options.AddPolicy("Enrollment", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});

var keyRingPath = ExpandPath(builder.Configuration["DataProtection:KeyRingPath"]
    ?? Path.Combine(AppContext.BaseDirectory, "data-protection-keys"));
Directory.CreateDirectory(keyRingPath);
builder.Services.AddDataProtection()
    .SetApplicationName("CompanyDlp.AdminApi")
    .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath));

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddSingleton<PolicySigningService>();
builder.Services.AddSingleton<AuditIntegrityValidator>();
builder.Services.AddSingleton<FileKeyEnvelopeService>();
builder.Services.AddScoped<PolicyCompiler>();
builder.Services.AddScoped<PolicyRevisionService>();
builder.Services.AddScoped<DevicePermissionAuthorizer>();
builder.Services.AddScoped<AdminAuditWriter>();

var app = builder.Build();
app.UseExceptionHandler();
app.UseCors("AdminPortal");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<AgentAuthenticationMiddleware>();

app.MapGet("/health", async (CompanyDlpDbContext db, CancellationToken ct) =>
{
    var databaseAvailable = await db.Database.CanConnectAsync(ct);
    return Results.Ok(new
    {
        status = databaseAvailable ? "Healthy" : "Degraded",
        databaseAvailable,
        serverTimeUtc = DateTimeOffset.UtcNow
    });
});
app.MapAuthEndpoints();
app.MapAdminUserEndpoints();
app.MapAdminManagementEndpoints();
app.MapAgentEndpoints();

var databaseOptions = builder.Configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();
if (databaseOptions.AutoMigrate)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<CompanyDlpDbContext>();
    await db.Database.MigrateAsync();
}

await app.RunAsync();

static string ExpandPath(string value)
{
    var expanded = Environment.ExpandEnvironmentVariables(value);
    return Path.GetFullPath(expanded);
}
