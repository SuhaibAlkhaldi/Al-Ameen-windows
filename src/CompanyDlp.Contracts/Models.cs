namespace CompanyDlp.Contracts;

public sealed class ClassificationRequest
{
    public string Text { get; set; } = "";
    public string Channel { get; set; } = "unknown";
    public string SessionKey { get; set; } = "default";
    public bool TrackFragments { get; set; }
}

public sealed class ClassificationResult
{
    public bool IsSensitive { get; set; }
    public bool FragmentAssemblyDetected { get; set; }
    public List<ClassificationMatch> Matches { get; set; } = [];
}

public sealed class ClassificationMatch
{
    public string RuleId { get; set; } = "";
    public string RuleName { get; set; } = "";
    public string MatchType { get; set; } = "";
    public string MaskedPreview { get; set; } = "";
}

public sealed class AuditEvent
{
    public Guid EventId { get; set; } = Guid.NewGuid();
    public Guid CorrelationId { get; set; } = Guid.NewGuid();
    public string ActionKey { get; set; } = "";
    public string ReasonCode { get; set; } = "";
    public DateTimeOffset OccurredAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string EventType { get; set; } = "";
    public string Action { get; set; } = "";
    public string Result { get; set; } = "";
    public string RuleId { get; set; } = "";
    public string Details { get; set; } = "";
    public string Destination { get; set; } = "";
    public string DeviceInstanceId { get; set; } = "";
    public string Method { get; set; } = "";
    public string SourceProcessName { get; set; } = "";
    public string SourceProcessPath { get; set; } = "";
    public int? SourceProcessId { get; set; }
    public string SourceProcessPublisher { get; set; } = "";
    public string SourceProcessSha256 { get; set; } = "";
    public string ResourceName { get; set; } = "";
    public string ResourceExtension { get; set; } = "";
    public long? ResourceSizeBytes { get; set; }
    public string ResourceSha256 { get; set; } = "";
    public string UserSid { get; set; } = "";
    public int WindowsSessionId { get; set; }
    public Guid? PermissionGrantId { get; set; }
    public string Username { get; set; } = Environment.UserName;
    public string MachineName { get; set; } = Environment.MachineName;
}

public sealed class ServiceStatus
{
    public string Version { get; set; } = "3.0.0";
    public DateTimeOffset StartedAtUtc { get; set; }
    public string Mode { get; set; } = "Development";
    public string PolicyPath { get; set; } = "";
    public string EffectiveUsbMode { get; set; } = "AuditOnly";
    public Guid DeviceId { get; set; }
    public long RemotePolicyVersion { get; set; }
    public int PendingAuditEventCount { get; set; }
    public string BackendMode { get; set; } = "Disabled";
}

public sealed class UsbDeviceBundleInfo
{
    public string RootInstanceId { get; set; } = "";
    public string DisplayName { get; set; } = "Unknown USB device";
    public List<string> Classes { get; set; } = [];
    public List<string> DeviceIds { get; set; } = [];
    public string Manufacturer { get; set; } = "";
    public string VendorId { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public string HardwareId { get; set; } = "";
    public bool IsCompositeDevice { get; set; }
    public bool HasKeyboardOrMouse { get; set; }
    public bool HasForbiddenFunction { get; set; }
    public bool IsTrustedBaseline { get; set; }
    public bool IsAllowed { get; set; }
}

public sealed class TemporaryUsbBlockRequest
{
    public int Minutes { get; set; } = 5;
}

public sealed class NotificationPollRequest
{
    public long AfterId { get; set; }
}

public sealed class UserNotification
{
    public long Id { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string Category { get; set; } = "security";
    public string Title { get; set; } = "Company DLP";
    public string Message { get; set; } = "An action was blocked by company policy.";
    public string Severity { get; set; } = "Warning";
    public string Action { get; set; } = "blocked";
}


public sealed class FileProtectionRequest
{
    public string Action { get; set; } = "";
    public string FilePath { get; set; } = "";
}

public sealed class FileProtectionResponse
{
    public Guid TransactionId { get; set; }
    public bool Success { get; set; }
    public string OutputPath { get; set; } = "";
    public string ErrorCode { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class AuditOutboxStatus
{
    public int PendingCount { get; set; }
    public int DeadLetterCount { get; set; }
    public DateTimeOffset? LastSuccessfulSyncAtUtc { get; set; }
    public string LastSyncError { get; set; } = "";
}
