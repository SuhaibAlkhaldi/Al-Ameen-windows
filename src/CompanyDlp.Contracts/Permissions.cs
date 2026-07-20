namespace CompanyDlp.Contracts;

public static class PermissionSubjectTypes
{
    public const string Global = "Global";
    public const string UserSid = "UserSid";
    public const string Username = "Username";
    public const string DeviceId = "DeviceId";
    public const string MachineName = "MachineName";
    public const string Group = "Group";
    public const string Department = "Department";
}

public static class PermissionSources
{
    public const string GlobalDefault = "GlobalDefault";
    public const string PermanentPolicy = "PermanentPolicy";
    public const string TemporaryGrant = "TemporaryGrant";
    public const string EmergencyDeny = "EmergencyDeny";
}

public sealed class PermissionPolicy
{
    public Dictionary<string, bool> DefaultPermissions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<PermissionGrant> Grants { get; set; } = [];
}

public sealed class PermissionGrant
{
    public Guid GrantId { get; set; } = Guid.NewGuid();
    public string ActionKey { get; set; } = "";
    public bool Allowed { get; set; }
    public string SubjectType { get; set; } = PermissionSubjectTypes.Global;
    public string SubjectId { get; set; } = "*";
    public string Source { get; set; } = PermissionSources.PermanentPolicy;
    public int Priority { get; set; } = 100;
    public DateTimeOffset StartsAtUtc { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public string Reason { get; set; } = "";
    public string GrantedBy { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public string RevokedBy { get; set; } = "";
}

public sealed class PermissionEvaluationRequest
{
    public string ActionKey { get; set; } = "";
    public ClientContext Context { get; set; } = new();
}

public sealed class PermissionDecision
{
    public string ActionKey { get; set; } = "";
    public bool IsAllowed { get; set; }
    public string ReasonCode { get; set; } = "GlobalDefault";
    public Guid? PermissionGrantId { get; set; }
    public DateTimeOffset? PermissionExpiresAtUtc { get; set; }
    public string PermissionSource { get; set; } = PermissionSources.GlobalDefault;
}
