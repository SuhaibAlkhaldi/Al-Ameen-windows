using CompanyDlp.Contracts;

namespace CompanyDlp.AdminApi.Endpoints;

public sealed class OnboardingRequest
{
    public string TenantName { get; set; } = "";
    public string AdminDisplayName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class LoginRequest
{
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class LoginResponse
{
    public string AccessToken { get; set; } = "";
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public Guid TenantId { get; set; }
    public Guid AdminUserId { get; set; }
    public string Role { get; set; } = "";
    public string DisplayName { get; set; } = "";
}

public sealed class EmployeeUpsertRequest
{
    public string EmployeeNumber { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Username { get; set; } = "";
    public string WindowsSid { get; set; } = "";
    public string Department { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public sealed class DeviceAssignmentRequest
{
    public Guid? EmployeeId { get; set; }
}

public sealed class EnrollmentCodeCreateRequest
{
    public string Description { get; set; } = "";
    public int? ValidForMinutes { get; set; }
}

public sealed class EnrollmentCodeCreateResponse
{
    public Guid Id { get; set; }
    public string EnrollmentCode { get; set; } = "";
    public DateTimeOffset ExpiresAtUtc { get; set; }
}

public sealed class PermissionGrantUpsertRequest
{
    public string ActionKey { get; set; } = "";
    public bool Allowed { get; set; }
    public string ScopeType { get; set; } = "Global";
    public string ScopeId { get; set; } = "*";
    public int Priority { get; set; } = 100;
    public DateTimeOffset? StartsAtUtc { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public string Reason { get; set; } = "";
    public bool EmergencyDeny { get; set; }
}

public sealed class UpdateTenantPolicyRequest
{
    public DlpPolicy Policy { get; set; } = new();
}

public sealed class AdminUserCreateRequest
{
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Password { get; set; } = "";
    public string Role { get; set; } = "PolicyAdmin";
}

public sealed class AdminUserUpdateRequest
{
    public string DisplayName { get; set; } = "";
    public string Role { get; set; } = "PolicyAdmin";
    public bool IsActive { get; set; } = true;
    public string? NewPassword { get; set; }
}
