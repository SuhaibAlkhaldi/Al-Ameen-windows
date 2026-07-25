using System.Text.Json;
using CompanyDlp.AdminApi.Data;
using CompanyDlp.AdminApi.Domain;
using CompanyDlp.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CompanyDlp.AdminApi.Services;

/// <summary>
/// Re-checks high-value backend operations against the authoritative central policy.
/// Endpoint enforcement remains active as the first layer; this is defense in depth.
/// </summary>
public sealed class DevicePermissionAuthorizer(CompanyDlpDbContext db)
{
    public async Task<PermissionDecision> EvaluateAsync(
        Guid tenantId,
        Guid deviceId,
        string actionKey,
        CancellationToken cancellationToken)
    {
        var device = await db.Devices.AsNoTracking()
            .Include(value => value.Employee)
            .SingleAsync(
                value => value.Id == deviceId && value.TenantId == tenantId && value.IsActive,
                cancellationToken);
        var policyJson = await db.TenantPolicies.AsNoTracking()
            .Where(value => value.TenantId == tenantId)
            .Select(value => value.PolicyJson)
            .SingleAsync(cancellationToken);
        var policy = JsonSerializer.Deserialize<DlpPolicy>(policyJson, JsonDefaults.Options)
            ?? throw new InvalidOperationException("The stored tenant policy JSON is invalid.");
        var validationError = TenantPolicySanitizer.Normalize(policy);
        if (validationError is not null)
            throw new InvalidOperationException($"The stored tenant policy is invalid: {validationError}.");

        var now = DateTimeOffset.UtcNow;
        var candidates = await db.PermissionGrants.AsNoTracking()
            .Where(value => value.TenantId == tenantId && value.ActionKey == actionKey)
            .Where(value => value.RevokedAtUtc == null && value.StartsAtUtc <= now)
            .Where(value => value.ExpiresAtUtc == null || value.ExpiresAtUtc > now)
            .ToListAsync(cancellationToken);

        var selected = candidates
            .Where(value => AppliesToDevice(value, device))
            .OrderByDescending(IsEmergencyDeny)
            .ThenByDescending(value => value.Priority)
            .ThenByDescending(value => Specificity(value, device))
            .ThenByDescending(value => value.CreatedAtUtc)
            .FirstOrDefault();

        if (selected is not null)
        {
            return new PermissionDecision
            {
                ActionKey = actionKey,
                IsAllowed = selected.Allowed,
                ReasonCode = IsEmergencyDeny(selected)
                    ? "EmergencyDeny"
                    : selected.Source.Equals(PermissionSources.TemporaryGrant, StringComparison.OrdinalIgnoreCase)
                        ? "TemporaryPermissionActive"
                        : "PermissionGrantMatched",
                PermissionGrantId = selected.Id,
                PermissionExpiresAtUtc = selected.ExpiresAtUtc,
                PermissionSource = selected.Source
            };
        }

        var allowed = policy.Permissions.DefaultPermissions.TryGetValue(actionKey, out var configured) && configured;
        return new PermissionDecision
        {
            ActionKey = actionKey,
            IsAllowed = allowed,
            ReasonCode = allowed ? "GlobalDefaultAllow" : "GlobalDefaultDeny",
            PermissionSource = PermissionSources.GlobalDefault
        };
    }

    private static bool AppliesToDevice(PermissionGrantEntity grant, DeviceEntity device)
    {
        if (grant.ScopeType.Equals(AdminPermissionScopeTypes.Global, StringComparison.OrdinalIgnoreCase))
            return true;
        if (grant.ScopeType.Equals(AdminPermissionScopeTypes.Device, StringComparison.OrdinalIgnoreCase))
            return Guid.TryParse(grant.ScopeId, out var id) && id == device.Id;
        if (grant.ScopeType.Equals(AdminPermissionScopeTypes.MachineName, StringComparison.OrdinalIgnoreCase))
            return grant.ScopeId.Equals(device.MachineName, StringComparison.OrdinalIgnoreCase);
        if (device.Employee?.IsActive != true) return false;
        if (grant.ScopeType.Equals(AdminPermissionScopeTypes.Employee, StringComparison.OrdinalIgnoreCase))
            return Guid.TryParse(grant.ScopeId, out var employeeId) && employeeId == device.EmployeeId;
        if (grant.ScopeType.Equals(AdminPermissionScopeTypes.Department, StringComparison.OrdinalIgnoreCase))
            return grant.ScopeId.Equals(device.Employee.Department, StringComparison.OrdinalIgnoreCase);
        if (grant.ScopeType.Equals(AdminPermissionScopeTypes.UserSid, StringComparison.OrdinalIgnoreCase))
            return grant.ScopeId.Equals(device.Employee.WindowsSid, StringComparison.OrdinalIgnoreCase);
        if (grant.ScopeType.Equals(AdminPermissionScopeTypes.Username, StringComparison.OrdinalIgnoreCase))
            return grant.ScopeId.Equals(device.Employee.Username, StringComparison.OrdinalIgnoreCase);
        return false;
    }

    private static bool IsEmergencyDeny(PermissionGrantEntity grant) =>
        !grant.Allowed && grant.Source.Equals(PermissionSources.EmergencyDeny, StringComparison.OrdinalIgnoreCase);

    private static int Specificity(PermissionGrantEntity grant, DeviceEntity device)
    {
        if (grant.ScopeType.Equals(AdminPermissionScopeTypes.UserSid, StringComparison.OrdinalIgnoreCase)) return 50;
        if (grant.ScopeType.Equals(AdminPermissionScopeTypes.Username, StringComparison.OrdinalIgnoreCase)) return 40;
        if (grant.ScopeType.Equals(AdminPermissionScopeTypes.Device, StringComparison.OrdinalIgnoreCase)) return 30;
        if (grant.ScopeType.Equals(AdminPermissionScopeTypes.MachineName, StringComparison.OrdinalIgnoreCase)) return 20;
        if (grant.ScopeType.Equals(AdminPermissionScopeTypes.Employee, StringComparison.OrdinalIgnoreCase)
            || grant.ScopeType.Equals(AdminPermissionScopeTypes.Department, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(device.Employee?.WindowsSid)) return 50;
            if (!string.IsNullOrWhiteSpace(device.Employee?.Username)) return 40;
            return 30;
        }
        return 0;
    }
}
