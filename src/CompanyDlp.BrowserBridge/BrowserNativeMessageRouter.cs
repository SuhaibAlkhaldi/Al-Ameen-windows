using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using CompanyDlp.Contracts;

namespace CompanyDlp.BrowserBridge;

public static class BrowserNativeMessageRouter
{
    public static async Task<object> HandleAsync(
        JsonElement message,
        string? sourceProcessNameOverride = null,
        int? sourceProcessIdOverride = null,
        CancellationToken cancellationToken = default)
    {
        var type = message.TryGetProperty("type", out var typeElement)
            ? typeElement.GetString() ?? ""
            : "";

        if (type.Equals("getIdentity", StringComparison.OrdinalIgnoreCase))
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new
            {
                success = true,
                username = identity.Name ?? $"{Environment.UserDomainName}\\{Environment.UserName}",
                userSid = identity.User?.Value ?? "",
                machineName = Environment.MachineName,
                windowsSessionId = Process.GetCurrentProcess().SessionId
            };
        }

        if (type.Equals("getPolicy", StringComparison.OrdinalIgnoreCase))
            return await SendPipeAsync(DlpMessageTypes.GetPolicy, cancellationToken: cancellationToken);

        if (type.Equals("classifyText", StringComparison.OrdinalIgnoreCase))
        {
            return await SendPipeAsync(DlpMessageTypes.ClassifyText, new ClassificationRequest
            {
                Text = ReadString(message, "text", ""),
                Channel = ReadString(message, "channel", "browser"),
                SessionKey = $"browser:{Environment.UserDomainName}\\{Environment.UserName}",
                TrackFragments = ReadBoolean(message, "trackFragments")
            }, cancellationToken);
        }

        if (type.Equals("classifyFile", StringComparison.OrdinalIgnoreCase))
        {
            return await SendPipeAsync(DlpMessageTypes.ClassifyFile, new FileClassificationRequest
            {
                FileName = ReadString(message, "fileName", ""),
                Extension = ReadString(message, "extension", ""),
                SizeBytes = ReadInt64(message, "sizeBytes") ?? 0,
                MimeType = ReadString(message, "mimeType", ""),
                Sha256 = ReadString(message, "sha256", ""),
                Channel = ReadString(message, "channel", "browser-upload"),
                Destination = ReadString(message, "destination", "")
            }, cancellationToken);
        }

        if (type.Equals("audit", StringComparison.OrdinalIgnoreCase))
        {
            var sourceProcessId = sourceProcessIdOverride ?? GetParentProcessId();
            var sourceProcessName = sourceProcessNameOverride ?? GetProcessName(sourceProcessId);
            return await SendPipeAsync(DlpMessageTypes.Audit, new AuditEvent
            {
                EventType = ReadString(message, "eventType", "browser"),
                Action = ReadString(message, "action", "unknown"),
                Method = ReadString(message, "method", ReadString(message, "action", "unknown")),
                Result = ReadString(message, "result", "unknown"),
                ReasonCode = ReadString(message, "reasonCode", "DeniedByBrowserPolicy"),
                RuleId = ReadString(message, "ruleId", ""),
                Details = ReadString(message, "details", ""),
                Destination = ReadString(message, "destination", ""),
                ResourceName = ReadString(message, "resourceName", ""),
                ResourceExtension = ReadString(message, "resourceExtension", ""),
                ResourceSizeBytes = ReadInt64(message, "resourceSizeBytes"),
                SourceProcessName = sourceProcessName,
                SourceProcessId = sourceProcessId
            }, cancellationToken);
        }

        return new { success = false, message = $"Unknown native message type: {type}" };
    }

    private static async Task<DlpResponse> SendPipeAsync(
        string type,
        object? data = null,
        CancellationToken cancellationToken = default)
    {
        await using var pipe = new NamedPipeClientStream(
            ".",
            PipeNames.Policy,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Impersonation);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await pipe.ConnectAsync(timeout.Token);

        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        var request = new DlpRequest
        {
            Type = type,
            Context = CreateContext(),
            Data = data is null ? null : JsonSerializer.SerializeToElement(data, JsonDefaults.Options)
        };
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonDefaults.Options));
        var response = await reader.ReadLineAsync(timeout.Token);
        return string.IsNullOrWhiteSpace(response)
            ? DlpResponse.Fail("Empty response from Company DLP service")
            : JsonSerializer.Deserialize<DlpResponse>(response, JsonDefaults.Options)
              ?? DlpResponse.Fail("Invalid service response");
    }

    private static ClientContext CreateContext()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new ClientContext
        {
            UserSid = identity.User?.Value ?? "",
            Username = identity.Name ?? $"{Environment.UserDomainName}\\{Environment.UserName}",
            MachineName = Environment.MachineName,
            WindowsSessionId = Process.GetCurrentProcess().SessionId,
            ClientName = "CompanyDlp.BrowserBridge",
            ClientVersion = "1.0.0"
        };
    }

    private static bool ReadBoolean(JsonElement message, string name) =>
        message.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
        && value.GetBoolean();

    private static long? ReadInt64(JsonElement message, string name)
    {
        if (!message.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
    }

    private static string ReadString(JsonElement message, string name, string fallback) =>
        message.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static int? GetParentProcessId()
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT ParentProcessId FROM Win32_Process WHERE ProcessId={current.Id}");
            foreach (System.Management.ManagementObject item in searcher.Get())
                return Convert.ToInt32((uint)item["ParentProcessId"]);
        }
        catch { }
        return null;
    }

    private static string GetProcessName(int? processId)
    {
        if (processId is null) return "browser";
        try
        {
            using var process = Process.GetProcessById(processId.Value);
            return process.ProcessName + ".exe";
        }
        catch { return "browser"; }
    }
}
