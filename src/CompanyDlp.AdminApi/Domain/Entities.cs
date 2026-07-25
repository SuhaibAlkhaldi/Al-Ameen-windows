using System.ComponentModel.DataAnnotations;

namespace CompanyDlp.AdminApi.Domain;

public static class AdminRoles
{
    public const string Owner = "Owner";
    public const string PolicyAdmin = "PolicyAdmin";
    public const string Auditor = "Auditor";
}

public static class AdminPermissionScopeTypes
{
    public const string Global = "Global";
    public const string Employee = "Employee";
    public const string Device = "Device";
    public const string Department = "Department";
    public const string UserSid = "UserSid";
    public const string Username = "Username";
    public const string MachineName = "MachineName";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Global, Employee, Device, Department, UserSid, Username, MachineName
    };
}

public sealed class TenantEntity
{
    public Guid Id { get; set; }
    [MaxLength(200)] public string Name { get; set; } = "";
    public long PolicyRevision { get; set; } = 1;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    [Timestamp] public byte[] RowVersion { get; set; } = [];
    public TenantPolicyEntity? Policy { get; set; }
}

public sealed class TenantPolicyEntity
{
    public Guid TenantId { get; set; }
    public Guid PolicyId { get; set; }
    public string PolicyJson { get; set; } = "";
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public Guid? UpdatedByAdminUserId { get; set; }
    public TenantEntity Tenant { get; set; } = null!;
}

public sealed class AdminUserEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    [MaxLength(320)] public string Email { get; set; } = "";
    [MaxLength(320)] public string NormalizedEmail { get; set; } = "";
    [MaxLength(200)] public string DisplayName { get; set; } = "";
    public string PasswordHashBase64 { get; set; } = "";
    public string PasswordSaltBase64 { get; set; } = "";
    [MaxLength(50)] public string Role { get; set; } = AdminRoles.PolicyAdmin;
    public int TokenVersion { get; set; } = 1;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAtUtc { get; set; }
}

public sealed class EmployeeEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    [MaxLength(100)] public string EmployeeNumber { get; set; } = "";
    [MaxLength(200)] public string DisplayName { get; set; } = "";
    [MaxLength(256)] public string Username { get; set; } = "";
    [MaxLength(256)] public string WindowsSid { get; set; } = "";
    [MaxLength(200)] public string Department { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<DeviceEntity> Devices { get; set; } = [];
}

public sealed class DeviceEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? EmployeeId { get; set; }
    [MaxLength(256)] public string MachineName { get; set; } = "";
    [MaxLength(50)] public string AgentVersion { get; set; } = "";
    [MaxLength(500)] public string OsVersion { get; set; } = "";
    [MaxLength(128)] public string TokenHashHex { get; set; } = "";
    public DateTimeOffset? TokenExpiresAtUtc { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset EnrolledAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSeenAtUtc { get; set; }
    public long LastAppliedPolicyVersion { get; set; }
    public int PendingAuditEventCount { get; set; }
    public EmployeeEntity? Employee { get; set; }
}

public sealed class EnrollmentCodeEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    [MaxLength(128)] public string CodeHashHex { get; set; } = "";
    [MaxLength(200)] public string Description { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? UsedAtUtc { get; set; }
    public Guid CreatedByAdminUserId { get; set; }
}

public sealed class PermissionGrantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    [MaxLength(200)] public string ActionKey { get; set; } = "";
    public bool Allowed { get; set; }
    [MaxLength(50)] public string ScopeType { get; set; } = AdminPermissionScopeTypes.Global;
    [MaxLength(300)] public string ScopeId { get; set; } = "*";
    [MaxLength(50)] public string Source { get; set; } = "PermanentPolicy";
    public int Priority { get; set; } = 100;
    public DateTimeOffset StartsAtUtc { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    [MaxLength(1000)] public string Reason { get; set; } = "";
    [MaxLength(320)] public string GrantedBy { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevokedAtUtc { get; set; }
    [MaxLength(320)] public string RevokedBy { get; set; } = "";
}

public sealed class SecurityEventEntity
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid EventId { get; set; }
    public Guid DeviceId { get; set; }
    public Guid CorrelationId { get; set; }
    public Guid? UserId { get; set; }
    [MaxLength(200)] public string ActionKey { get; set; } = "";
    [MaxLength(200)] public string EventType { get; set; } = "";
    [MaxLength(50)] public string Decision { get; set; } = "";
    [MaxLength(200)] public string ReasonCode { get; set; } = "";
    public DateTimeOffset OccurredAtUtc { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string PayloadJson { get; set; } = "";
}

public sealed class AdminAuditLogEntity
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? AdminUserId { get; set; }
    [MaxLength(320)] public string AdminEmail { get; set; } = "";
    [MaxLength(200)] public string Action { get; set; } = "";
    [MaxLength(100)] public string TargetType { get; set; } = "";
    [MaxLength(300)] public string TargetId { get; set; } = "";
    public string DetailsJson { get; set; } = "{}";
    [MaxLength(100)] public string IpAddress { get; set; } = "";
    public DateTimeOffset OccurredAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
