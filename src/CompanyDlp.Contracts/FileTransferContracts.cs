namespace CompanyDlp.Contracts;

// One observed send of one file to one recipient (Phase 7). The browser sends the raw observation only; the backend
// decides evidence levels and whether the file is sensitive (see FileTransferEvidenceRules on the backend).
public sealed class FileTransferObservationNotice
{
    public Guid SendActionId { get; set; }

    public int RecipientOrdinal { get; set; }

    public string Channel { get; set; } = "";

    public string? RecipientRole { get; set; }

    public string RecipientKind { get; set; } = "";

    public string RecipientValue { get; set; } = "";

    public string RecipientEvidenceType { get; set; } = "";

    public string FileName { get; set; } = "";

    public long FileSizeBytes { get; set; }

    public string? FileHashBytes { get; set; }

    public string? ContentFingerprint { get; set; }

    public DateTimeOffset ObservedAtUtc { get; set; }

    public string? ExtensionVersion { get; set; }
}

// Wire shape of one observation inside the backend batch (api/v1/agent/file-transfer-events/batch). Field names match the
// backend's FileTransferObservationDto so the JSON binds without a mapping step.
public sealed class FileTransferEventBatchRequest
{
    public Guid TenantId { get; set; }

    public Guid DeviceId { get; set; }

    public List<FileTransferObservationNotice> Events { get; set; } = [];
}

// The backend's reply carries only per-decision counts, and the agent does not need them: a success response means the
// batch is final. Only the success flag is read.
public sealed class FileTransferBatchAck
{
    public bool Success { get; set; }
}
