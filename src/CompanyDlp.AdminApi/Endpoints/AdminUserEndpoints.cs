using System.Data;
using System.Net.Mail;
using CompanyDlp.AdminApi.Data;
using CompanyDlp.AdminApi.Domain;
using CompanyDlp.AdminApi.Security;
using CompanyDlp.AdminApi.Services;
using Microsoft.EntityFrameworkCore;

namespace CompanyDlp.AdminApi.Endpoints;

public static class AdminUserEndpoints
{
    private static readonly IReadOnlySet<string> Roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        AdminRoles.Owner,
        AdminRoles.PolicyAdmin,
        AdminRoles.Auditor
    };

    public static IEndpointRouteBuilder MapAdminUserEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var owner = endpoints.MapGroup("/api/v1/admin/admin-users").RequireAuthorization("OwnerOnly");
        owner.MapGet("", GetAsync);
        owner.MapPost("", CreateAsync);
        owner.MapPut("/{adminUserId:guid}", UpdateAsync);
        return endpoints;
    }

    private static async Task<IResult> GetAsync(HttpContext http, CompanyDlpDbContext db, CancellationToken ct)
    {
        var tenantId = AdminClaims.GetTenantId(http.User);
        var values = await db.AdminUsers.AsNoTracking()
            .Where(value => value.TenantId == tenantId)
            .OrderBy(value => value.DisplayName)
            .Select(value => new
            {
                value.Id,
                value.Email,
                value.DisplayName,
                value.Role,
                value.IsActive,
                value.CreatedAtUtc,
                value.LastLoginAtUtc
            })
            .ToListAsync(ct);
        return Results.Ok(values);
    }

    private static async Task<IResult> CreateAsync(
        AdminUserCreateRequest request,
        HttpContext http,
        CompanyDlpDbContext db,
        PasswordHasher passwordHasher,
        AdminAuditWriter auditWriter,
        CancellationToken ct)
    {
        var validation = ValidateCreate(request);
        if (validation is not null) return Results.BadRequest(new { error = validation });

        var normalizedEmail = request.Email.Trim().ToUpperInvariant();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (await db.AdminUsers.AnyAsync(value => value.NormalizedEmail == normalizedEmail, ct))
            return Results.Conflict(new { error = "AdminEmailAlreadyExists" });

        var role = CanonicalRole(request.Role);
        var (hash, salt) = passwordHasher.Hash(request.Password);
        var tenantId = AdminClaims.GetTenantId(http.User);
        var entity = new AdminUserEntity
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Email = request.Email.Trim(),
            NormalizedEmail = normalizedEmail,
            DisplayName = request.DisplayName.Trim(),
            PasswordHashBase64 = hash,
            PasswordSaltBase64 = salt,
            Role = role,
            IsActive = true,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        db.AdminUsers.Add(entity);
        await db.SaveChangesAsync(ct);
        await auditWriter.WriteAsync(
            tenantId,
            "AdminUserCreated",
            "AdminUser",
            entity.Id.ToString("D"),
            new { entity.Email, entity.DisplayName, entity.Role },
            ct);
        await transaction.CommitAsync(ct);
        return Results.Created($"/api/v1/admin/admin-users/{entity.Id:D}", Project(entity));
    }

    private static async Task<IResult> UpdateAsync(
        Guid adminUserId,
        AdminUserUpdateRequest request,
        HttpContext http,
        CompanyDlpDbContext db,
        PasswordHasher passwordHasher,
        AdminAuditWriter auditWriter,
        CancellationToken ct)
    {
        var validation = ValidateUpdate(request);
        if (validation is not null) return Results.BadRequest(new { error = validation });
        var tenantId = AdminClaims.GetTenantId(http.User);
        var currentAdminId = AdminClaims.GetAdminUserId(http.User);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var entity = await db.AdminUsers.SingleOrDefaultAsync(
            value => value.Id == adminUserId && value.TenantId == tenantId,
            ct);
        if (entity is null) return Results.NotFound();

        var role = CanonicalRole(request.Role);
        if (entity.Id == currentAdminId && (!request.IsActive || !role.Equals(AdminRoles.Owner, StringComparison.Ordinal)))
            return Results.Conflict(new { error = "OwnerCannotDeactivateOrDemoteCurrentSession" });

        var removesOwner = entity.IsActive
            && entity.Role.Equals(AdminRoles.Owner, StringComparison.OrdinalIgnoreCase)
            && (!request.IsActive || !role.Equals(AdminRoles.Owner, StringComparison.Ordinal));
        if (removesOwner)
        {
            var otherActiveOwnerExists = await db.AdminUsers.AnyAsync(
                value => value.TenantId == tenantId
                    && value.Id != entity.Id
                    && value.IsActive
                    && value.Role == AdminRoles.Owner,
                ct);
            if (!otherActiveOwnerExists)
                return Results.Conflict(new { error = "AtLeastOneActiveOwnerRequired" });
        }

        var securityStateChanged = !entity.Role.Equals(role, StringComparison.Ordinal)
            || entity.IsActive != request.IsActive
            || !string.IsNullOrEmpty(request.NewPassword);
        entity.DisplayName = request.DisplayName.Trim();
        entity.Role = role;
        entity.IsActive = request.IsActive;
        if (!string.IsNullOrEmpty(request.NewPassword))
        {
            var (hash, salt) = passwordHasher.Hash(request.NewPassword);
            entity.PasswordHashBase64 = hash;
            entity.PasswordSaltBase64 = salt;
        }
        if (securityStateChanged) entity.TokenVersion++;
        await db.SaveChangesAsync(ct);
        await auditWriter.WriteAsync(
            tenantId,
            "AdminUserUpdated",
            "AdminUser",
            entity.Id.ToString("D"),
            new { entity.Email, entity.DisplayName, entity.Role, entity.IsActive, PasswordReset = !string.IsNullOrEmpty(request.NewPassword) },
            ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(Project(entity));
    }

    private static string? ValidateCreate(AdminUserCreateRequest request)
    {
        if (!IsValidEmail(request.Email) || request.Email.Trim().Length > 320) return "ValidAdminEmailRequired";
        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length > 200) return "AdminDisplayNameRequired";
        if (string.IsNullOrEmpty(request.Password) || request.Password.Length < 12 || request.Password.Length > 1024) return "AdminPasswordLengthInvalid";
        if (!Roles.Contains(request.Role?.Trim() ?? "")) return "UnsupportedAdminRole";
        return null;
    }

    private static string? ValidateUpdate(AdminUserUpdateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length > 200) return "AdminDisplayNameRequired";
        if (!Roles.Contains(request.Role?.Trim() ?? "")) return "UnsupportedAdminRole";
        if (request.NewPassword is not null && request.NewPassword.Length > 0
            && (request.NewPassword.Length < 12 || request.NewPassword.Length > 1024))
            return "AdminPasswordLengthInvalid";
        return null;
    }

    private static string CanonicalRole(string role) => Roles.First(value => value.Equals(role.Trim(), StringComparison.OrdinalIgnoreCase));

    private static bool IsValidEmail(string value)
    {
        try { return new MailAddress(value).Address.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static object Project(AdminUserEntity entity) => new
    {
        entity.Id,
        entity.Email,
        entity.DisplayName,
        entity.Role,
        entity.IsActive,
        entity.CreatedAtUtc,
        entity.LastLoginAtUtc
    };
}
