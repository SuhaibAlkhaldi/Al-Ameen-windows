using System.Data;
using System.Net.Mail;
using System.Text.Json;
using CompanyDlp.AdminApi.Configuration;
using CompanyDlp.AdminApi.Data;
using CompanyDlp.AdminApi.Domain;
using CompanyDlp.AdminApi.Security;
using CompanyDlp.AdminApi.Services;
using CompanyDlp.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CompanyDlp.AdminApi.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/admin");
        group.MapPost("/onboarding/register", RegisterAsync).AllowAnonymous().RequireRateLimiting("Auth");
        group.MapPost("/auth/login", LoginAsync).AllowAnonymous().RequireRateLimiting("Auth");
        return endpoints;
    }

    private static async Task<IResult> RegisterAsync(
        OnboardingRequest request,
        CompanyDlpDbContext db,
        PasswordHasher passwordHasher,
        JwtTokenService jwtTokenService,
        AdminAuditWriter auditWriter,
        IOptions<OnboardingOptions> onboardingOptions,
        IOptions<PolicyDeliveryOptions> deliveryOptions,
        CancellationToken cancellationToken)
    {
        var validation = ValidateOnboarding(request);
        if (validation is not null) return Results.BadRequest(new { error = validation });

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var hasTenant = await db.Tenants.AnyAsync(cancellationToken);
        if (hasTenant && !onboardingOptions.Value.AllowPublicOnboarding)
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var normalizedEmail = NormalizeEmail(request.Email);
        if (await db.AdminUsers.AnyAsync(value => value.NormalizedEmail == normalizedEmail, cancellationToken))
            return Results.Conflict(new { error = "AdminEmailAlreadyExists" });

        var now = DateTimeOffset.UtcNow;
        var tenant = new TenantEntity
        {
            Id = Guid.NewGuid(),
            Name = request.TenantName.Trim(),
            PolicyRevision = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        var (hash, salt) = passwordHasher.Hash(request.Password);
        var admin = new AdminUserEntity
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Email = request.Email.Trim(),
            NormalizedEmail = normalizedEmail,
            DisplayName = request.AdminDisplayName.Trim(),
            PasswordHashBase64 = hash,
            PasswordSaltBase64 = salt,
            Role = AdminRoles.Owner,
            CreatedAtUtc = now
        };
        var policy = DefaultPolicyFactory.Create(tenant.Id, deliveryOptions.Value);
        var policyRecord = new TenantPolicyEntity
        {
            TenantId = tenant.Id,
            PolicyId = Guid.NewGuid(),
            PolicyJson = JsonSerializer.Serialize(policy, JsonDefaults.Options),
            UpdatedAtUtc = now,
            UpdatedByAdminUserId = admin.Id
        };

        db.Tenants.Add(tenant);
        db.AdminUsers.Add(admin);
        db.TenantPolicies.Add(policyRecord);
        await db.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(
            tenant.Id,
            "TenantOnboarded",
            "Tenant",
            tenant.Id.ToString("D"),
            new { tenant.Name, admin.Email },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var (token, expires) = jwtTokenService.Create(admin);
        return Results.Created($"/api/v1/admin/tenants/{tenant.Id:D}", new LoginResponse
        {
            AccessToken = token,
            ExpiresAtUtc = expires,
            TenantId = tenant.Id,
            AdminUserId = admin.Id,
            Role = admin.Role,
            DisplayName = admin.DisplayName
        });
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        CompanyDlpDbContext db,
        PasswordHasher passwordHasher,
        JwtTokenService jwtTokenService,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password)
            || request.Email.Trim().Length > 320 || request.Password.Length > 1024)
            return Results.BadRequest(new { error = "EmailAndPasswordRequired" });

        var normalizedEmail = NormalizeEmail(request.Email);
        var user = await db.AdminUsers.SingleOrDefaultAsync(
            value => value.NormalizedEmail == normalizedEmail && value.IsActive,
            cancellationToken);
        if (user is null || !passwordHasher.Verify(request.Password, user.PasswordHashBase64, user.PasswordSaltBase64))
            return Results.Unauthorized();

        var tenantActive = await db.Tenants.AnyAsync(
            value => value.Id == user.TenantId && value.IsActive,
            cancellationToken);
        if (!tenantActive) return Results.Unauthorized();

        user.LastLoginAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        var (token, expires) = jwtTokenService.Create(user);
        return Results.Ok(new LoginResponse
        {
            AccessToken = token,
            ExpiresAtUtc = expires,
            TenantId = user.TenantId,
            AdminUserId = user.Id,
            Role = user.Role,
            DisplayName = user.DisplayName
        });
    }

    private static string? ValidateOnboarding(OnboardingRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TenantName) || request.TenantName.Trim().Length > 200)
            return "A tenant name up to 200 characters is required.";
        if (string.IsNullOrWhiteSpace(request.AdminDisplayName) || request.AdminDisplayName.Trim().Length > 200)
            return "An admin display name up to 200 characters is required.";
        if (!IsValidEmail(request.Email) || request.Email.Trim().Length > 320)
            return "A valid admin email address up to 320 characters is required.";
        if (string.IsNullOrEmpty(request.Password) || request.Password.Length < 12 || request.Password.Length > 1024)
            return "The admin password must contain between 12 and 1024 characters.";
        return null;
    }

    private static bool IsValidEmail(string value)
    {
        try { return new MailAddress(value).Address.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static string NormalizeEmail(string value) => value.Trim().ToUpperInvariant();
}
