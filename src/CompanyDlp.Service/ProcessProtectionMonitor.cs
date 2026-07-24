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

        if (!policy.Enabled || !policy.Screen.Enabled) return;
        if (!policy.Screen.MonitorKnownRecorderProcesses && !policy.Screen.MonitorKnownScreenshotToolProcesses) return;

        var watchLists = new[]
        {
            new ProcessWatch(
                Enabled: policy.Screen.MonitorKnownRecorderProcesses,
                Names: policy.Screen.BlockedRecorderProcessNames,
                ActionKey: ActionKeys.ScreenRecording,
                CapabilityLabel: "screen recording",
                NounSingular: "Screen recording application"),
            new ProcessWatch(
                Enabled: policy.Screen.MonitorKnownScreenshotToolProcesses,
                Names: policy.Screen.BlockedScreenshotToolProcessNames,
                ActionKey: ActionKeys.ScreenCapture,
                CapabilityLabel: "screenshot capture",
                NounSingular: "Screenshot tool")
        };

        var targets = watchLists
            .Where(w => w.Enabled)
            .SelectMany(w => w.Names.Select(name => (Name: Path.GetFileNameWithoutExtension(name).Trim(), Watch: w)))
            .ToDictionary(x => x.Name, x => x.Watch, StringComparer.OrdinalIgnoreCase);
        var activeTargetIds = new HashSet<int>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string name;
                try { name = Path.GetFileNameWithoutExtension(process.ProcessName).Trim(); }
                catch { continue; }
                if (!targets.TryGetValue(name, out var watch)) continue;
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
                    watch.ActionKey,
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
                        logger.LogWarning(exception, "Could not terminate process {ProcessName}", name);
                        result = "termination-failed";
                    }
                }

                await auditLogger.WriteAsync(new AuditEvent
                {
                    ActionKey = watch.ActionKey,
                    EventType = decision.IsAllowed ? "ScreenProcessAllowed" : result == "terminated" ? "ScreenProcessBlocked" : "ScreenProcessDetected",
                    Action = "process-detected",
                    Method = watch.ActionKey == ActionKeys.ScreenRecording ? "KnownRecorderProcess" : "KnownScreenshotToolProcess",
                    Result = result,
                    ReasonCode = decision.IsAllowed ? decision.ReasonCode : result == "terminated" ? "ProcessDeniedByPolicy" : result == "termination-failed" ? "ProcessTerminationFailed" : "ProcessAuditOnly",
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
                    "terminated" => $"{watch.NounSingular} blocked",
                    "termination-failed" => $"{watch.NounSingular} could not be stopped",
                    _ => $"{watch.NounSingular} detected"
                };
                var message = result switch
                {
                    "terminated" => $"{name} was closed because {watch.CapabilityLabel} is prohibited by company policy.",
                    "termination-failed" => $"{name} was detected, but Company DLP could not close it. Contact IT.",
                    _ => $"{name} was detected while {watch.CapabilityLabel} protection is enabled."
                };
                notificationStore.Add(
                    watch.ActionKey == ActionKeys.ScreenRecording ? "screen-recorder" : "screenshot-tool",
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

    private sealed record ProcessWatch(
        bool Enabled,
        List<string> Names,
        string ActionKey,
        string CapabilityLabel,
        string NounSingular);
}
