using System.Text.Json;

namespace CompanyDlp.Contracts;

public static class PipeNames
{
    public const string Policy = "CompanyDlp.Policy.v2";
}

public static class DlpMessageTypes
{
    public const string Ping = "ping";
    public const string GetPolicy = "getPolicy";
    public const string ReloadPolicy = "reloadPolicy";
    public const string ClassifyText = "classifyText";
    public const string ClassifyFile = "classifyFile";
    public const string Audit = "audit";
    public const string ApplyBrowserPolicies = "applyBrowserPolicies";
    public const string GetUsbSnapshot = "getUsbSnapshot";
    public const string SetTemporaryUsbBlock = "setTemporaryUsbBlock";
    public const string ResetUsbBaseline = "resetUsbBaseline";
    public const string GetUserNotifications = "getUserNotifications";
    public const string EvaluatePermission = "evaluatePermission";
    public const string ProtectFile = "protectFile";
    public const string GetOutboxStatus = "getOutboxStatus";
    public const string GetFileClassificationStatus = "getFileClassificationStatus";

    // Reported by the browser extension (service-worker.js) the moment chrome.downloads reports a
    // download's state as "complete" - final absolute path included. Feeds FileProvenanceStore's
    // BrowserDownload channel; see ActionKeys.FileOpenAccess's comment for the full design.
    public const string DownloadCompleted = "downloadCompleted";

    // Sent by the Desktop app's --request-access CLI verb (ShellCryptoCommandRunner), reached via the
    // .dlpenc ProgID's default "open" command - see FileProtectionCoordinator.ExecuteOpenAccessAsync.
    public const string RequestFileOpenAccess = "requestFileOpenAccess";

    // Phase 7: a browser send observed by the Gmail content script (see FileTransferContracts.cs).
    public const string FileTransferObserved = "fileTransferObserved";
}

public sealed class DlpRequest
{
    public string ProtocolVersion { get; set; } = "1.0";
    public Guid MessageId { get; set; } = Guid.NewGuid();
    public DateTimeOffset SentAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string Type { get; set; } = "";
    public ClientContext Context { get; set; } = new();
    public JsonElement? Data { get; set; }
}

public sealed class DlpResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public JsonElement? Data { get; set; }

    public static DlpResponse Ok(string message = "OK", object? data = null) => new()
    {
        Success = true,
        Message = message,
        Data = data is null ? null : JsonSerializer.SerializeToElement(data, JsonDefaults.Options)
    };

    public static DlpResponse Fail(string message, object? data = null) => new()
    {
        Success = false,
        Message = message,
        Data = data is null ? null : JsonSerializer.SerializeToElement(data, JsonDefaults.Options)
    };
}
