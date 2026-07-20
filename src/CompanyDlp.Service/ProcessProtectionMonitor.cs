using System.Diagnostics;
using System.Management;
using System.Security.Cryptography;
using CompanyDlp.Contracts;

namespace CompanyDlp.Service;

public sealed class ProcessProtectionMonitor(
    PolicyStore policyStore,
    AgentIdentityProvider identityProvider,
    PermissionEvaluator permissionEvaluator,
    AuditLogger auditLogger,
    NotificationStore notificationStore,
    ILogger<ProcessProtectionMonitor> logger)
{
    private readonly HashSet<int> _reported = [];

    public async Task TickAsync(CancellationToken cancellationToken)
    {
        var policy = policyStore.Get();

        // In Development the interactive Desktop session owns this control, so temporary
        // per-user permissions can be applied and removed without a service restart.
        if (policy.Runtime.Mode.Equals("Development", StringComparison.OrdinalIgnoreCase))
        {
            _reported.Clear();
            return;
        }

        if (!policy.Enabled || !policy.Screen.Enabled || !policy.Screen.MonitorKnownRecorderProcesses) return;

        var targets = policy.Screen.BlockedProcessNames
            .Select(name => Path.GetFileNameWithoutExtension(name).Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var activeTargetIds = new HashSet<int>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string name;
                try { name = Path.GetFileNameWithoutExtension(process.ProcessName).Trim(); }
                catch { continue; }
                if (!targets.Contains(name)) continue;
                activeTargetIds.Add(process.Id);
                if (!_reported.Add(process.Id)) continue;

                var metadata = TryGetMetadata(process.Id, name);
                var context = new ClientContext
                {
                    UserSid = metadata.UserSid,
                    Username = metadata.Username,
                    MachineName = Environment.MachineName,
                    WindowsSessionId = SafeSessionId(process),
                    ClientName = metadata.Name,
                    ClientVersion = "1.0.0"
                };
                var decision = permissionEvaluator.Evaluate(
                    policy,
                    ActionKeys.ScreenRecording,
                    context,
                    identityProvider.Get(),
                    DateTimeOffset.UtcNow);

                var result = decision.IsAllowed ? "allowed" : "detected";
                if (!decision.IsAllowed && policy.Screen.RecorderEnforcementMode.Equals("Block", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        process.Kill(true);
                        result = "terminated";
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(exception, "Could not terminate recorder process {ProcessName}", name);
                        result = "termination-failed";
                    }
                }

                await auditLogger.WriteAsync(new AuditEvent
                {
                    ActionKey = ActionKeys.ScreenRecording,
                    EventType = decision.IsAllowed ? "ScreenRecordingAllowed" : result == "terminated" ? "ScreenRecordingBlocked" : "ScreenRecordingApplicationDetected",
                    Action = "process-detected",
                    Method = "KnownRecorderProcess",
                    Result = result,
                    ReasonCode = decision.IsAllowed ? decision.ReasonCode : result == "terminated" ? "RecorderProcessDeniedByPolicy" : result == "termination-failed" ? "RecorderTerminationFailed" : "RecorderAuditOnly",
                    PermissionGrantId = decision.PermissionGrantId,
                    SourceProcessName = metadata.Name,
                    SourceProcessPath = metadata.Path,
                    SourceProcessId = process.Id,
                    SourceProcessSha256 = await TryComputeSha256Async(metadata.Path, cancellationToken),
                    Details = name
                }, context, cancellationToken);

                if (decision.IsAllowed) continue;

                var title = result switch
                {
                    "terminated" => "Screen recording application blocked",
                    "termination-failed" => "Screen recording application could not be stopped",
                    _ => "Screen recording application detected"
                };
                var message = result switch
                {
                    "terminated" => $"{name} was closed because screen recording is prohibited by company policy.",
                    "termination-failed" => $"{name} was detected, but Company DLP could not close it. Contact IT.",
                    _ => $"{name} was detected while screen recording protection is enabled."
                };
                notificationStore.Add(
                    "screen-recorder",
                    title,
                    message,
                    result == "termination-failed" ? "Error" : "Warning",
                    result);
            }
        }

        _reported.RemoveWhere(id => !activeTargetIds.Contains(id));
    }

    private static ProcessMetadata TryGetMetadata(int processId, string fallbackName)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT Name, ExecutablePath FROM Win32_Process WHERE ProcessId={processId}");
            foreach (ManagementObject item in searcher.Get())
            {
                var sid = "";
                var username = "";
                try
                {
                    var sidOutput = new object[] { "" };
                    if (Convert.ToUInt32(item.InvokeMethod("GetOwnerSid", sidOutput)) == 0) sid = sidOutput[0]?.ToString() ?? "";
                    var ownerOutput = new object[] { "", "" };
                    if (Convert.ToUInt32(item.InvokeMethod("GetOwner", ownerOutput)) == 0) username = $"{ownerOutput[1]}\\{ownerOutput[0]}".Trim('\\');
                }
                catch { }

                return new ProcessMetadata(
                    item["Name"]?.ToString() ?? fallbackName,
                    item["ExecutablePath"]?.ToString() ?? "",
                    sid,
                    username);
            }
        }
        catch { }
        return new ProcessMetadata(fallbackName, "", "", "");
    }

    private static async Task<string> TryComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return "";
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        }
        catch { return ""; }
    }

    private static int SafeSessionId(Process process)
    {
        try { return process.SessionId; } catch { return 0; }
    }

    private sealed record ProcessMetadata(string Name, string Path, string UserSid, string Username);
}
