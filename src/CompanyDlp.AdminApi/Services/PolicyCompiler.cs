using System.Text.Json;
using CompanyDlp.AdminApi.Configuration;
using CompanyDlp.AdminApi.Data;
using CompanyDlp.AdminApi.Domain;
using CompanyDlp.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CompanyDlp.AdminApi.Services;

public sealed class PolicyCompiler(
    CompanyDlpDbContext db,
    PolicySigningService signingService,
    IOptions<PolicyDeliveryOptions> deliveryOptions)
{
    private readonly PolicyDeliveryOptions _delivery = deliveryOptions.Value;

    public async Task<SignedPolicySnapshot> BuildSnapshotAsync(
        Guid tenantId,
        Guid deviceId,
        CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.AsNoTracking()
            .SingleAsync(value => value.Id == tenantId && value.IsActive, cancellationToken);
        var policyRecord = await db.TenantPolicies.AsNoTracking()
            .SingleAsync(value => value.TenantId == tenantId, cancellationToken);
        var device = await db.Devices.AsNoTracking()
            .Include(value => value.Employee)
            .SingleAsync(value => value.Id == deviceId && value.TenantId == tenantId && value.IsActive, cancellationToken);

        var policy = JsonSerializer.Deserialize<DlpPolicy>(policyRecord.PolicyJson, JsonDefaults.Options)
            ?? throw new InvalidOperationException("The stored tenant policy JSON is invalid.");
        var validationError = TenantPolicySanitizer.Normalize(policy);
        if (validationError is not null)
            throw new InvalidOperationException($"The stored tenant policy is invalid: {validationError}.");
        policy.PolicyVersion = $"central-{tenant.PolicyRevision}";
        policy.Runtime.Mode = _delivery.Mode;
        policy.Runtime.PersistentProtection = string.Equals(_delivery.Mode, "Production", StringComparison.OrdinalIgnoreCase);
        policy.Backend.Enabled = true;
        policy.Backend.TenantId = tenantId;
        policy.Backend.Mode = _delivery.Mode;
        policy.Backend.BaseUrl = _delivery.PublicBaseUrl;
        policy.Backend.AuthenticationMode = BackendAuthenticationModes.DeviceBearerToken;
        policy.Backend.CredentialName = "agent-access-token";
        policy.Backend.AllowUnsignedDevelopmentPolicy = _delivery.AllowUnsignedDevelopmentSnapshots;
        policy.Backend.PolicySigningPublicKeyPem = signingService.GetPublicKeyPem();
        policy.FileProtection.KeyProvider = "BackendKms";
        policy.FileClassification.Enabled = true;
        policy.FileClassification.Provider = FileClassificationProviders.BlockAll;
        policy.FileClassification.FailClosed = true;
        policy.FileClassification.BackendPath = "api/v1/agent/file-classification";
        policy.Permissions.Grants = [];

        var now = DateTimeOffset.UtcNow;
        var grants = await db.PermissionGrants.AsNoTracking()
            .Where(value => value.TenantId == tenantId && value.RevokedAtUtc == null)
            .Where(value => value.StartsAtUtc <= now)
            .Where(value => value.ExpiresAtUtc == null || value.ExpiresAtUtc > now)
            .ToListAsync(cancellationToken);

        foreach (var entity in grants)
        {
            var mapped = MapGrant(entity, device);
            if (mapped is not null) policy.Permissions.Grants.Add(mapped);
        }

        var snapshot = new SignedPolicySnapshot
        {
            PolicyId = policyRecord.PolicyId,
            Version = tenant.PolicyRevision,
            TenantId = tenantId,
            DeviceId = deviceId,
            IssuedAtUtc = now,
            ExpiresAtUtc = now.AddMinutes(Math.Clamp(_delivery.SnapshotLifetimeMinutes, 10, 10080)),
            Policy = policy
        };
        signingService.Sign(snapshot);
        return snapshot;
    }

    private static PermissionGrant? MapGrant(PermissionGrantEntity entity, DeviceEntity device)
    {
        string subjectType;
        string subjectId;

        if (entity.ScopeType.Equals(AdminPermissionScopeTypes.Global, StringComparison.OrdinalIgnoreCase))
        {
            subjectType = PermissionSubjectTypes.Global;
            subjectId = "*";
        }
        else if (entity.ScopeType.Equals(AdminPermissionScopeTypes.Device, StringComparison.OrdinalIgnoreCase))
        {
            if (!Guid.TryParse(entity.ScopeId, out var targetDeviceId) || targetDeviceId != device.Id) return null;
            subjectType = PermissionSubjectTypes.DeviceId;
            subjectId = device.Id.ToString("D");
        }
        else if (entity.ScopeType.Equals(AdminPermissionScopeTypes.Employee, StringComparison.OrdinalIgnoreCase))
        {
            if (device.EmployeeId is null || device.Employee?.IsActive != true
                || !Guid.TryParse(entity.ScopeId, out var targetEmployeeId)
                || targetEmployeeId != device.EmployeeId)
                return null;
            (subjectType, subjectId) = ResolveEmployeeSubject(device);
        }
        else if (entity.ScopeType.Equals(AdminPermissionScopeTypes.Department, StringComparison.OrdinalIgnoreCase))
        {
            if (device.Employee?.IsActive != true
                || !entity.ScopeId.Equals(device.Employee.Department, StringComparison.OrdinalIgnoreCase))
                return null;
            (subjectType, subjectId) = ResolveEmployeeSubject(device);
        }
        else if (entity.ScopeType.Equals(AdminPermissionScopeTypes.UserSid, StringComparison.OrdinalIgnoreCase))
        {
            if (device.Employee?.IsActive != true || !entity.ScopeId.Equals(device.Employee.WindowsSid, StringComparison.OrdinalIgnoreCase)) return null;
            subjectType = PermissionSubjectTypes.UserSid;
            subjectId = entity.ScopeId;
        }
        else if (entity.ScopeType.Equals(AdminPermissionScopeTypes.Username, StringComparison.OrdinalIgnoreCase))
        {
            if (device.Employee?.IsActive != true || !entity.ScopeId.Equals(device.Employee.Username, StringComparison.OrdinalIgnoreCase)) return null;
            subjectType = PermissionSubjectTypes.Username;
            subjectId = entity.ScopeId;
        }
        else if (entity.ScopeType.Equals(AdminPermissionScopeTypes.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            if (!entity.ScopeId.Equals(device.MachineName, StringComparison.OrdinalIgnoreCase)) return null;
            subjectType = PermissionSubjectTypes.MachineName;
            subjectId = device.MachineName;
        }
        else
        {
            return null;
        }

        return new PermissionGrant
        {
            GrantId = entity.Id,
            ActionKey = entity.ActionKey,
            Allowed = entity.Allowed,
            SubjectType = subjectType,
            SubjectId = subjectId,
            Source = entity.Source,
            Priority = entity.Priority,
            StartsAtUtc = entity.StartsAtUtc,
            ExpiresAtUtc = entity.ExpiresAtUtc,
            Reason = entity.Reason,
            GrantedBy = entity.GrantedBy,
            CreatedAtUtc = entity.CreatedAtUtc,
            RevokedAtUtc = entity.RevokedAtUtc,
            RevokedBy = entity.RevokedBy
        };
    }

    private static (string SubjectType, string SubjectId) ResolveEmployeeSubject(DeviceEntity device)
    {
        if (!string.IsNullOrWhiteSpace(device.Employee?.WindowsSid))
            return (PermissionSubjectTypes.UserSid, device.Employee.WindowsSid);
        if (!string.IsNullOrWhiteSpace(device.Employee?.Username))
            return (PermissionSubjectTypes.Username, device.Employee.Username);
        return (PermissionSubjectTypes.DeviceId, device.Id.ToString("D"));
    }
}
