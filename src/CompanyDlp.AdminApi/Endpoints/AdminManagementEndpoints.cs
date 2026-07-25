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

public static class AdminManagementEndpoints
{
    public static IEndpointRouteBuilder MapAdminManagementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var policyAdmin = endpoints.MapGroup("/api/v1/admin").RequireAuthorization("PolicyAdmin");
        policyAdmin.MapGet("/actions", () => Results.Ok(ActionKeys.All.OrderBy(value => value)));
        policyAdmin.MapGet("/employees", GetEmployeesAsync);
        policyAdmin.MapPost("/employees", CreateEmployeeAsync);
        policyAdmin.MapPut("/employees/{employeeId:guid}", UpdateEmployeeAsync);
        policyAdmin.MapGet("/devices", GetDevicesAsync);
        policyAdmin.MapPut("/devices/{deviceId:guid}/assignment", AssignDeviceAsync);
        policyAdmin.MapPost("/devices/{deviceId:guid}/revoke", RevokeDeviceAsync);
        policyAdmin.MapPost("/enrollment-codes", CreateEnrollmentCodeAsync);
        policyAdmin.MapGet("/permissions", GetPermissionsAsync);
        policyAdmin.MapPost("/permissions", CreatePermissionAsync);
        policyAdmin.MapPut("/permissions/{grantId:guid}", UpdatePermissionAsync);
        policyAdmin.MapDelete("/permissions/{grantId:guid}", RevokePermissionAsync);
        policyAdmin.MapGet("/policy", GetPolicyAsync);
        policyAdmin.MapPut("/policy", UpdatePolicyAsync);

        var audit = endpoints.MapGroup("/api/v1/admin").RequireAuthorization("AuditRead");
        audit.MapGet("/audit-events", GetAuditEventsAsync);
        audit.MapGet("/admin-audit", GetAdminAuditAsync);
        return endpoints;
    }

    private static async Task<IResult> GetEmployeesAsync(HttpContext http, CompanyDlpDbContext db, CancellationToken ct)
    {
        var tenantId = AdminClaims.GetTenantId(http.User);
        var values = await db.Employees.AsNoTracking()
            .Where(value => value.TenantId == tenantId)
            .OrderBy(value => value.DisplayName)
            .Select(value => new
            {
                value.Id,
                value.EmployeeNumber,
                value.DisplayName,
                value.Username,
                value.WindowsSid,
                value.Department,
                value.IsActive,
                deviceCount = value.Devices.Count
            })
            .ToListAsync(ct);
        return Results.Ok(values);
    }

    private static async Task<IResult> CreateEmployeeAsync(
        EmployeeUpsertRequest request,
        HttpContext http,
        CompanyDlpDbContext db,
        PolicyRevisionService revisionService,
        AdminAuditWriter auditWriter,
        CancellationToken ct)
    {
        var tenantId = AdminClaims.GetTenantId(http.User);
        var error = ValidateEmployee(request);
        if (error is not null) return Results.BadRequest(new { error });
        var number = request.EmployeeNumber.Trim();
        if (await db.Employees.AnyAsync(value => value.TenantId == tenantId && value.EmployeeNumber == number, ct))
            return Results.Conflict(new { error = "EmployeeNumberAlreadyExists" });

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var entity = new EmployeeEntity
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            EmployeeNumber = number,
            DisplayName = request.DisplayName.Trim(),
            Username = (request.Username ?? "").Trim(),
            WindowsSid = (request.WindowsSid ?? "").Trim(),
            Department = (request.Department ?? "").Trim(),
            IsActive = request.IsActive
        };
        db.Employees.Add(entity);
        await db.SaveChangesAsync(ct);
        await revisionService.IncrementAsync(tenantId, ct);
        await auditWriter.WriteAsync(tenantId, "EmployeeCreated", "Employee", entity.Id.ToString("D"), new { entity.EmployeeNumber, entity.DisplayName }, ct);
        await transaction.CommitAsync(ct);
        return Results.Created($"/api/v1/admin/employees/{entity.Id:D}", entity);
    }

    private static async Task<IResult> UpdateEmployeeAsync(
        Guid employeeId,
        EmployeeUpsertRequest request,
        HttpContext http,
        CompanyDlpDbContext db,
        PolicyRevisionService revisionService,
        AdminAuditWriter auditWriter,
        CancellationToken ct)
    {
        var tenantId = AdminClaims.GetTenantId(http.User);
        var error = ValidateEmployee(request);
        if (error is not null) return Results.BadRequest(new { error });
        var entity = await db.Employees.SingleOrDefaultAsync(value => value.Id == employeeId && value.TenantId == tenantId, ct);
        if (entity is null) return Results.NotFound();
        var number = request.EmployeeNumber.Trim();
        if (await db.Employees.AnyAsync(value => value.TenantId == tenantId && value.EmployeeNumber == number && value.Id != employeeId, ct))
            return Results.Conflict(new { error = "EmployeeNumberAlreadyExists" });

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        entity.EmployeeNumber = number;
        entity.DisplayName = request.DisplayName.Trim();
        entity.Username = (request.Username ?? "").Trim();
        entity.WindowsSid = (request.WindowsSid ?? "").Trim();
        entity.Department = (request.Department ?? "").Trim();
        entity.IsActive = request.IsActive;
        entity.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await revisionService.IncrementAsync(tenantId, ct);
        await auditWriter.WriteAsync(tenantId, "EmployeeUpdated", "Employee", entity.Id.ToString("D"), new { entity.EmployeeNumber, entity.DisplayName }, ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(entity);
    }

    private static async Task<IResult> GetDevicesAsync(HttpContext http, CompanyDlpDbContext db, CancellationToken ct)
    {
        var tenantId = AdminClaims.GetTenantId(http.User);
        var values = await db.Devices.AsNoTracking()
            .Where(value => value.TenantId == tenantId)
            .OrderByDescending(value => value.LastSeenAtUtc)
            .Select(value => new
            {
                value.Id,
                value.MachineName,
                value.AgentVersion,
                value.OsVersion,
                value.EmployeeId,
                employeeName = value.Employee == null ? null : value.Employee.DisplayName,
                value.IsActive,
                value.EnrolledAtUtc,
                value.LastSeenAtUtc,
                value.LastAppliedPolicyVersion,
                value.PendingAuditEventCount,
                tokenExpiresAtUtc = value.TokenExpiresAtUtc
            })
            .ToListAsync(ct);
        return Results.Ok(values);
    }

    private static async Task<IResult> AssignDeviceAsync(
        Guid deviceId,
        DeviceAssignmentRequest request,
        HttpContext http,
        CompanyDlpDbContext db,
        PolicyRevisionService revisionService,
        AdminAuditWriter auditWriter,
        CancellationToken ct)
    {
        var tenantId = AdminClaims.GetTenantId(http.User);
        var device = await db.Devices.SingleOrDefaultAsync(value => value.Id == deviceId && value.TenantId == tenantId, ct);
        if (device is null) return Results.NotFound();
        if (request.EmployeeId is { } employeeId
            && !await db.Employees.AnyAsync(value => value.Id == employeeId && value.TenantId == tenantId && value.IsActive, ct))
            return Results.BadRequest(new { error = "EmployeeNotFoundOrInactive" });

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        device.EmployeeId = request.EmployeeId;
        await db.SaveChangesAsync(ct);
        await revisionService.IncrementAsync(tenantId, ct);
        await auditWriter.WriteAsync(tenantId, "DeviceAssignmentChanged", "Device", device.Id.ToString("D"), new { device.EmployeeId }, ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> RevokeDeviceAsync(
        Guid deviceId,
        HttpContext http,
        CompanyDlpDbContext db,
        PolicyRevisionService revisionService,
        AdminAuditWriter auditWriter,
        CancellationToken ct)
    {
        var tenantId = AdminClaims.GetTenantId(http.User);
        var device = await db.Devices.SingleOrDefaultAsync(value => value.Id == deviceId && value.TenantId == tenantId, ct);
        if (device is null) return Results.NotFound();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        device.IsActive = false;
        device.TokenHashHex = "";
        device.TokenExpiresAtUtc = null;
        await db.SaveChangesAsync(ct);
        await revisionService.IncrementAsync(tenantId, ct);
        await auditWriter.WriteAsync(tenantId, "DeviceRevoked", "Device", device.Id.ToString("D"), new { device.MachineName }, ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> CreateEnrollmentCodeAsync(
        EnrollmentCodeCreateRequest request,
        HttpContext http,
        CompanyDlpDbContext db,
        AdminAuditWriter auditWriter,
        IOptions<AgentSecurityOptions> securityOptions,
        CancellationToken ct)
    {
        var tenantId = AdminClaims.GetTenantId(http.User);
        var adminId = AdminClaims.GetAdminUserId(http.User);
        var description = (request.Description ?? "").Trim();
        if (description.Length > 200) return Results.BadRequest(new { error = "DescriptionTooLong" });
        var minutes = Math.Clamp(request.ValidForMinutes ?? securityOptions.Value.EnrollmentCodeMinutes, 5, 1440);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var code = TokenUtilities.CreateOpaqueToken(32);
        var entity = new EnrollmentCodeEntity
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CodeHashHex = TokenUtilities.HashToken(code),
            Description = description,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(minutes),
            CreatedByAdminUserId = adminId
        };
        db.EnrollmentCodes.Add(entity);
        await db.SaveChangesAsync(ct);
        await auditWriter.WriteAsync(tenantId, "EnrollmentCodeCreated", "EnrollmentCode", entity.Id.ToString("D"), new { entity.Description, entity.ExpiresAtUtc }, ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new EnrollmentCodeCreateResponse
        {
            Id = entity.Id,
            EnrollmentCode = code,
            ExpiresAtUtc = entity.ExpiresAtUtc
        });
    }

    private static async Task<IResult> GetPermissionsAsync(HttpContext http, CompanyDlpDbContext db, CancellationToken ct)
    {
        var tenantId = AdminClaims.GetTenantId(http.User);
        var values = await db.PermissionGrants.AsNoTracking()
            .Where(value => value.TenantId == tenantId)
            .OrderByDescending(value => value.CreatedAtUtc)
            .ToListAsync(ct);
        return Results.Ok(values);
    }

    private static async Task<IResult> CreatePermissionAsync(
        PermissionGrantUpsertRequest request,
        HttpContext http,
        CompanyDlpDbContext db,
        PolicyRevisionService revisionService,
        AdminAuditWriter auditWriter,
        CancellationToken ct)
    {
        var tenantId = AdminClaims.GetTenantId(http.User);
        var error = await ValidatePermissionAsync(request, tenantId, db, ct);
        if (error is not null) return Results.BadRequest(new { error });
        var now = DateTimeOffset.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var entity = new PermissionGrantEntity
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CreatedAtUtc = now,
            GrantedBy = AdminClaims.GetEmail(http.User)
        };
        ApplyPermission(entity, request, now);
        db.PermissionGrants.Add(entity);
        await db.SaveChangesAsync(ct);
        await revisionService.IncrementAsync(tenantId, ct);
        await auditWriter.WriteAsync(tenantId, "PermissionCreated", "PermissionGrant", entity.Id.ToString("D"), PermissionAuditDetails(entity), ct);
        await transaction.CommitAsync(ct);
        return Results.Created($"/api/v1/admin/permissions/{entity.Id:D}", entity);
    }

    private static async Task<IResult> UpdatePermissionAsync(
        Guid grantId,
        PermissionGrantUpsertRequest request,
        HttpContext http,
        CompanyDlpDbContext db,
        PolicyRevisionService revisionService,
        AdminAuditWriter auditWriter,
        CancellationToken ct)
    {
        var tenantId = AdminClaims.GetTenantId(http.User);
        var error = await ValidatePermissionAsync(request, tenantId, db, ct);
        if (error is not null) return Results.BadRequest(new { error });
        var entity = await db.PermissionGrants.SingleOrDefaultAsync(value => value.Id == grantId && value.TenantId == tenantId, ct);
        if (entity is null) return Results.NotFound();
        if (entity.RevokedAtUtc is not null) return Results.Conflict(new { error = "RevokedPermissionCannotBeEdited" });
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        ApplyPermission(entity, request, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(ct);
        await revisionService.IncrementAsync(tenantId, ct);
        await auditWriter.WriteAsync(tenantId, "PermissionUpdated", "PermissionGrant", entity.Id.ToString("D"), PermissionAuditDetails(entity), ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(entity);
    }

    private static async Task<IResult> RevokePermissionAsync(
        Guid grantId,
        HttpContext http,
        CompanyDlpDbContext db,
        PolicyRevisionService revisionService,
        AdminAuditWriter auditWriter,
        CancellationToken ct)
    {
        var tenantId = AdminClaims.GetTenantId(http.User);
        var entity = await db.PermissionGrants.SingleOrDefaultAsync(value => value.Id == grantId && value.TenantId == tenantId, ct);
        if (entity is null) return Results.NotFound();
        if (entity.RevokedAtUtc is null)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            entity.RevokedAtUtc = DateTimeOffset.UtcNow;
            entity.RevokedBy = AdminClaims.GetEmail(http.User);
            entity.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await revisionService.IncrementAsync(tenantId, ct);
            await auditWriter.WriteAsync(tenantId, "PermissionRevoked", "PermissionGrant", entity.Id.ToString("D"), PermissionAuditDetails(entity), ct);
            await transaction.CommitAsync(ct);
        }
        return Results.NoContent();
    }

    private static async Task<IResult> GetPolicyAsync(HttpContext http, CompanyDlpDbContext db, CancellationToken ct)
    {
        var tenantId = AdminClaims.GetTenantId(http.User);
        var value = await db.TenantPolicies.AsNoTracking().SingleAsync(item => item.TenantId == tenantId, ct);
        return Results.Ok(new
        {
            value.PolicyId,
            value.UpdatedAtUtc,
            policy = JsonSerializer.Deserialize<DlpPolicy>(value.PolicyJson, JsonDefaults.Options)
        });
    }

    private static async Task<IResult> UpdatePolicyAsync(
        UpdateTenantPolicyRequest request,
        HttpContext http,
        CompanyDlpDbContext db,
        PolicyRevisionService revisionService,
        AdminAuditWriter auditWriter,
        CancellationToken ct)
    {
        var tenantId = AdminClaims.GetTenantId(http.User);
        if (request.Policy is null) return Results.BadRequest(new { error = "PolicyRequired" });
        var validationError = TenantPolicySanitizer.Normalize(request.Policy);
        if (validationError is not null) return Results.BadRequest(new { error = validationError });
        request.Policy.Permissions.Grants = [];
        request.Policy.Backend.TenantId = tenantId;
        var serializedPolicy = JsonSerializer.Serialize(request.Policy, JsonDefaults.Options);
        if (System.Text.Encoding.UTF8.GetByteCount(serializedPolicy) > 1_048_576)
            return Results.BadRequest(new { error = "PolicyPayloadTooLarge" });
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var record = await db.TenantPolicies.SingleAsync(value => value.TenantId == tenantId, ct);
        record.PolicyJson = serializedPolicy;
        record.UpdatedAtUtc = DateTimeOffset.UtcNow;
        record.UpdatedByAdminUserId = AdminClaims.GetAdminUserId(http.User);
        await db.SaveChangesAsync(ct);
        var revision = await revisionService.IncrementAsync(tenantId, ct);
        await auditWriter.WriteAsync(tenantId, "TenantPolicyUpdated", "TenantPolicy", record.PolicyId.ToString("D"), new { revision }, ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new { record.PolicyId, revision, record.UpdatedAtUtc });
    }

    private static async Task<IResult> GetAuditEventsAsync(int? take, HttpContext http, CompanyDlpDbContext db, CancellationToken ct)
    {
        var tenantId = AdminClaims.GetTenantId(http.User);
        var maximum = Math.Clamp(take ?? 100, 1, 1000);
        var values = await db.SecurityEvents.AsNoTracking()
            .Where(value => value.TenantId == tenantId)
            .OrderByDescending(value => value.OccurredAtUtc)
            .Take(maximum)
            .Select(value => new
            {
                value.EventId,
                value.DeviceId,
                value.CorrelationId,
                value.UserId,
                value.ActionKey,
                value.EventType,
                value.Decision,
                value.ReasonCode,
                value.OccurredAtUtc,
                value.ReceivedAtUtc
            })
            .ToListAsync(ct);
        return Results.Ok(values);
    }

    private static async Task<IResult> GetAdminAuditAsync(int? take, HttpContext http, CompanyDlpDbContext db, CancellationToken ct)
    {
        var tenantId = AdminClaims.GetTenantId(http.User);
        var maximum = Math.Clamp(take ?? 100, 1, 1000);
        var values = await db.AdminAuditLogs.AsNoTracking()
            .Where(value => value.TenantId == tenantId)
            .OrderByDescending(value => value.OccurredAtUtc)
            .Take(maximum)
            .ToListAsync(ct);
        return Results.Ok(values);
    }

    private static string? ValidateEmployee(EmployeeUpsertRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.EmployeeNumber) || request.EmployeeNumber.Trim().Length > 100)
            return "EmployeeNumberRequired";
        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length > 200)
            return "DisplayNameRequired";
        if ((request.WindowsSid ?? "").Trim().Length > 256
            || (request.Username ?? "").Trim().Length > 256
            || (request.Department ?? "").Trim().Length > 200)
            return "EmployeeFieldTooLong";
        return null;
    }

    private static async Task<string?> ValidatePermissionAsync(
        PermissionGrantUpsertRequest request,
        Guid tenantId,
        CompanyDlpDbContext db,
        CancellationToken ct)
    {
        if (!ActionKeys.All.Contains(request.ActionKey?.Trim() ?? "")) return "UnsupportedActionKey";
        if (!AdminPermissionScopeTypes.All.Contains(request.ScopeType?.Trim() ?? "")) return "UnsupportedScopeType";
        if (request.Priority is < 0 or > 10000) return "PriorityOutOfRange";
        if ((request.ScopeId ?? "").Trim().Length > 300) return "ScopeIdTooLong";
        if ((request.Reason ?? "").Trim().Length > 1000) return "ReasonTooLong";
        var starts = request.StartsAtUtc ?? DateTimeOffset.UtcNow;
        if (request.ExpiresAtUtc is { } expires && expires <= starts) return "ExpiryMustBeAfterStart";
        if (request.EmergencyDeny && request.Allowed) return "EmergencyDenyCannotAllow";

        var scopeType = request.ScopeType?.Trim() ?? string.Empty;
        var scopeId = (request.ScopeId ?? "").Trim();
        if (scopeType.Equals(AdminPermissionScopeTypes.Global, StringComparison.OrdinalIgnoreCase)) return null;
        if (string.IsNullOrWhiteSpace(scopeId)) return "ScopeIdRequired";
        if (scopeType.Equals(AdminPermissionScopeTypes.Employee, StringComparison.OrdinalIgnoreCase))
            return Guid.TryParse(scopeId, out var employeeId)
                   && await db.Employees.AnyAsync(value => value.Id == employeeId && value.TenantId == tenantId, ct)
                ? null : "EmployeeScopeNotFound";
        if (scopeType.Equals(AdminPermissionScopeTypes.Device, StringComparison.OrdinalIgnoreCase))
            return Guid.TryParse(scopeId, out var deviceId)
                   && await db.Devices.AnyAsync(value => value.Id == deviceId && value.TenantId == tenantId, ct)
                ? null : "DeviceScopeNotFound";
        return null;
    }

    private static void ApplyPermission(PermissionGrantEntity entity, PermissionGrantUpsertRequest request, DateTimeOffset now)
    {
        entity.ActionKey = ActionKeys.All.First(value => value.Equals(request.ActionKey.Trim(), StringComparison.OrdinalIgnoreCase));
        entity.Allowed = request.Allowed;
        entity.ScopeType = AdminPermissionScopeTypes.All.First(value => value.Equals(request.ScopeType.Trim(), StringComparison.OrdinalIgnoreCase));
        entity.ScopeId = entity.ScopeType.Equals(AdminPermissionScopeTypes.Global, StringComparison.OrdinalIgnoreCase)
            ? "*"
            : (request.ScopeId ?? "").Trim();
        entity.Priority = request.Priority;
        entity.StartsAtUtc = request.StartsAtUtc ?? now;
        entity.ExpiresAtUtc = request.ExpiresAtUtc;
        entity.Reason = (request.Reason ?? "").Trim();
        entity.Source = request.EmergencyDeny
            ? PermissionSources.EmergencyDeny
            : request.ExpiresAtUtc is not null
                ? PermissionSources.TemporaryGrant
                : PermissionSources.PermanentPolicy;
        entity.UpdatedAtUtc = now;
    }

    private static object PermissionAuditDetails(PermissionGrantEntity entity) => new
    {
        entity.ActionKey,
        entity.Allowed,
        entity.ScopeType,
        entity.ScopeId,
        entity.Source,
        entity.Priority,
        entity.StartsAtUtc,
        entity.ExpiresAtUtc,
        entity.Reason,
        entity.RevokedAtUtc
    };
}

