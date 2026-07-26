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

    // Both null = ordinary action-level grant (unchanged behavior). FileHash set = grant covers
    // exactly one file (SHA-256, lowercase hex). ClassificationTier set (FileHash null) = grant
    // covers any file whose cached classification rank is <= this tier's rank (see
    // ClassificationTiers.RankOf). The two are mutually exclusive - a request is either scoped to
    // one exact file or to a tier, never both.
    public string? FileHash { get; set; }
    public string? ClassificationTier { get; set; }
}

public sealed class PermissionEvaluationRequest
{
    public string ActionKey { get; set; } = "";
    public ClientContext Context { get; set; } = new();

    // Set by the browser extension for browser.upload/browser.drag-drop checks so the evaluator
    // can match FileHash/ClassificationTier-scoped grants, not just action-level ones. Left null
    // for every other action key.
    public string? FileHash { get; set; }
}

public sealed class PermissionDecision
{
    public string ActionKey { get; set; } = "";
    public bool IsAllowed { get; set; }
    public string ReasonCode { get; set; } = "GlobalDefault";
    public Guid? PermissionGrantId { get; set; }
    public DateTimeOffset? PermissionExpiresAtUtc { get; set; }
    public string PermissionSource { get; set; } = PermissionSources.GlobalDefault;

    // Populated by PipeServer when the request carried a FileHash, from the local
    // FileClassificationCache - lets the browser extension show/report the file's actual
    // classification on a blocked attempt without a second round trip. Null when no fileHash was
    // given, or when that hash has no cached classification yet.
    public string? FileClassification { get; set; }
    public string? FileClassificationReasonCode { get; set; }
}
