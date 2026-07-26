namespace CompanyDlp.Contracts;

public static class FileClassificationProviders
{
    public const string BlockAll = "BlockAll";
    public const string AiApi = "AiApi";
}

public sealed class FileClassificationPolicy
{
    public bool Enabled { get; set; } = true;
    public string Provider { get; set; } = FileClassificationProviders.BlockAll;
    public bool FailClosed { get; set; } = true;
    public string BackendPath { get; set; } = "api/v1/agent/file-classification";
    public int TimeoutSeconds { get; set; } = 30;
    public long MaximumFileSizeBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    // Background inventory scan: classifies files proactively (as they appear/change) instead of
    // at upload time, so the enforcement-time check is always a fast local cache lookup. A file
    // with no cached result yet is treated as ClassificationTiers.VerySecret (fail-closed) until
    // the scanner catches up to it.
    public bool BackgroundScanEnabled { get; set; } = true;
    public List<string> WatchedFolders { get; set; } =
    [
        "%USERPROFILE%\\Downloads",
        "%USERPROFILE%\\Desktop",
        "%USERPROFILE%\\Documents"
    ];
    public int ScanIntervalSeconds { get; set; } = 10;
    public bool BackfillCompleted { get; set; }

    // Base URL of the employee-facing Angular portal (not the backend API - a separate deployment).
    // The browser extension appends /permission-requests/new?fromEvent=<correlationId> or
    // ?tier=<X>&actionKey=<key> to build the "Request Permission" deep link on a blocked attempt.
    // Sent to the browser as part of the full policy clone in EffectivePolicyBuilder.
    public string PortalBaseUrl { get; set; } = "";
}

// String constants for the AI classifier's tiers, matching http://.../api/v1/scan's exact
// `classification` values verbatim (not translated) - keeping them one-to-one with the API avoids
// a mapping layer that could drift out of sync silently.
public static class ClassificationTiers
{
    public const string Public = "Public";
    public const string Internal = "Internal";
    public const string Secret = "Secret";
    public const string VerySecret = "Very_Secret";

    // Ordered least to most sensitive - used to decide whether a ClassificationTier-scoped grant
    // (e.g. "Secret") covers a given file's classification (a "Secret" grant also covers Public
    // and Internal files, since those are less sensitive, but not Very_Secret).
    public static readonly IReadOnlyList<string> Order = [Public, Internal, Secret, VerySecret];

    // An unrecognized value (e.g. the BlockAll provider's placeholder "Sensitive" classification,
    // used before the real AI provider is wired in) must rank as the MOST sensitive tier, not the
    // least - IndexOf returning -1 must never resolve to Public's rank 0, or fail-closed callers
    // (PermissionEvaluator) would silently treat an unclassified/unrecognized file as safe.
    public static int RankOf(string tier)
    {
        var index = Order.ToList().IndexOf(tier);
        return index >= 0 ? index : Order.Count - 1;
    }
}

public sealed class FileClassificationRequest
{
    public Guid RequestId { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid DeviceId { get; set; }
    public string UserSid { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Extension { get; set; } = "";
    public long SizeBytes { get; set; }
    public string MimeType { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Channel { get; set; } = "browser-upload";
    public string Destination { get; set; } = "";
    public DateTimeOffset RequestedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class FileClassificationResult
{
    public Guid RequestId { get; set; }
    public bool IsAllowed { get; set; }
    public bool IsSensitive { get; set; } = true;
    public string Classification { get; set; } = "Sensitive";
    public string ReasonCode { get; set; } = "BlockAllUntilAiProviderAvailable";
    public string Provider { get; set; } = FileClassificationProviders.BlockAll;
    public string RuleId { get; set; } = "";
    public DateTimeOffset EvaluatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ValidUntilUtc { get; set; }
}
