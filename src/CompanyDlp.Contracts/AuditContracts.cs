using System.Text.Json;

namespace CompanyDlp.Contracts;

public enum EnforcementDecision
{
    Allow,
    Block,
    Audit,
    Error
}

public sealed class ProcessContext
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public int? ProcessId { get; set; }
    public string Publisher { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string WindowTitle { get; set; } = "";
}

public sealed class ResourceContext
{
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public string Extension { get; set; } = "";
    public long? SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string MaskedPath { get; set; } = "";
}

public sealed class DestinationContext
{
    public string Type { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class SecurityEventEnvelope
{
    public Guid EventId { get; set; } = Guid.NewGuid();
    public Guid CorrelationId { get; set; } = Guid.NewGuid();
    public string ProtocolVersion { get; set; } = "1.0";
    public string EventSchemaVersion { get; set; } = "1.0";
    public Guid TenantId { get; set; }
    public Guid DeviceId { get; set; }
    public Guid? UserId { get; set; }
    public string UserSid { get; set; } = "";
    public string Username { get; set; } = "";
    public string MachineName { get; set; } = "";
    public int WindowsSessionId { get; set; }
    public string ActionKey { get; set; } = "";
    public string EventType { get; set; } = "";
    public EnforcementDecision Decision { get; set; }
    public string ReasonCode { get; set; } = "";
    public Guid? PolicyId { get; set; }
    public long? PolicyVersion { get; set; }
    public string RuleId { get; set; } = "";
    public Guid? PermissionGrantId { get; set; }
    public ProcessContext? SourceProcess { get; set; }
    public ResourceContext? Resource { get; set; }
    public DestinationContext? Destination { get; set; }
    public JsonElement Details { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string AgentVersion { get; set; } = "1.0.0";
    public string OsVersion { get; set; } = Environment.OSVersion.VersionString;
    public bool IsDevelopmentEvent { get; set; }
    public string IntegrityHash { get; set; } = "";
}
