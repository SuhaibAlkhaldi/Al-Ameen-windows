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
