namespace CompanyDlp.Contracts;

public static class BackendAuthenticationModes
{
    public const string DevelopmentNone = "DevelopmentNone";
    public const string DeviceBearerToken = "DeviceBearerToken";
}

public sealed class BackendPolicy
{
    public bool Enabled { get; set; } = true;
    public Guid TenantId { get; set; }
    public string Mode { get; set; } = "Mock";
    public string BaseUrl { get; set; } = "http://127.0.0.1:5055";
    public int RequestTimeoutSeconds { get; set; } = 15;
    public int AuditBatchSize { get; set; } = 100;
    public int AuditSyncSeconds { get; set; } = 5;
    public int PolicySyncSeconds { get; set; } = 30;
    public int HeartbeatSeconds { get; set; } = 30;
    public bool AllowUnsignedDevelopmentPolicy { get; set; } = true;
    public string PolicySigningPublicKeyPem { get; set; } = "";
    public string AuthenticationMode { get; set; } = BackendAuthenticationModes.DevelopmentNone;
    public string CredentialName { get; set; } = "agent-access-token";
}

public sealed class AuditBatchRequest
{
    public Guid TenantId { get; set; }
    public Guid DeviceId { get; set; }
    public string AgentVersion { get; set; } = "1.0.0";
    public List<SecurityEventEnvelope> Events { get; set; } = [];
}

public sealed class AuditBatchResponse
{
    public List<Guid> AcceptedEventIds { get; set; } = [];
    public List<Guid> DuplicateEventIds { get; set; } = [];
    public List<RejectedAuditEvent> RejectedEvents { get; set; } = [];
}

public sealed class RejectedAuditEvent
{
    public Guid EventId { get; set; }
    public string ReasonCode { get; set; } = "";
    public bool Retryable { get; set; }
}

public sealed class AgentHeartbeatRequest
{
    public Guid TenantId { get; set; }
    public Guid DeviceId { get; set; }
    public string MachineName { get; set; } = "";
    public string AgentVersion { get; set; } = "1.0.0";
    public string OsVersion { get; set; } = "";

    // Distinct from OsVersion (Environment.OSVersion.VersionString, e.g. "Microsoft Windows NT
    // 10.0.26200.0" - never includes edition). This is the actual edition display name (e.g. "Windows
    // 11 Enterprise"), read from the registry by CliEnforcementHealthChecker.ReadEdition() - the same
    // read used to decide whether AppLocker enforcement is even possible on this device. Lets an admin
    // see which devices support CliExecute enforcement without RDPing into each one individually.
    public string OperatingSystemEdition { get; set; } = "";

    public DateTimeOffset SentAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public long LastAppliedPolicyVersion { get; set; }
    public int PendingAuditEventCount { get; set; }
}

public sealed class AgentHeartbeatResponse
{
    public DateTimeOffset ServerTimeUtc { get; set; } = DateTimeOffset.UtcNow;
    public bool PolicyRefreshRequired { get; set; }
}

public sealed class SignedPolicySnapshot
{
    public Guid PolicyId { get; set; }
    public long Version { get; set; }
    public Guid TenantId { get; set; }
    public Guid DeviceId { get; set; }
    public DateTimeOffset IssuedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DlpPolicy Policy { get; set; } = new();
    public string SignatureAlgorithm { get; set; } = "ECDSA-SHA256";
    public string SignatureBase64 { get; set; } = "";
}

public sealed class WrappedFileKey
{
    public string Provider { get; set; } = "";
    public string KeyId { get; set; } = "";
    public string WrappedKeyBase64 { get; set; } = "";
}

public sealed class FileKeyWrapRequest
{
    public Guid TenantId { get; set; }
    public Guid DeviceId { get; set; }
    public Guid FileId { get; set; }
    public string PlainKeyBase64 { get; set; } = "";
}

public sealed class FileKeyWrapResponse
{
    public string KeyId { get; set; } = "";
    public string WrappedKeyBase64 { get; set; } = "";
}

public sealed class FileKeyUnwrapRequest
{
    public Guid TenantId { get; set; }
    public Guid DeviceId { get; set; }
    public Guid FileId { get; set; }
    public string KeyId { get; set; } = "";
    public string WrappedKeyBase64 { get; set; } = "";
}

public sealed class FileKeyUnwrapResponse
{
    public string PlainKeyBase64 { get; set; } = "";
}

public sealed class AgentEnrollmentRequest
{
    public Guid TenantId { get; set; }
    public Guid DeviceId { get; set; }
    public string MachineName { get; set; } = "";
    public string AgentVersion { get; set; } = "";
    public string EnrollmentCode { get; set; } = "";
}

public sealed class AgentEnrollmentResponse
{
    public string AccessToken { get; set; } = "";
    public DateTimeOffset ExpiresAtUtc { get; set; }
}
