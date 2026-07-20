using System.Diagnostics;
using System.Management;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CompanyDlp.Contracts;
using CompanyDlp.Core;

namespace CompanyDlp.Service;

public sealed class SoftwareProtectionMonitor(
    PolicyStore policyStore,
    AgentIdentityProvider identityProvider,
    PermissionEvaluator permissionEvaluator,
    AuditLogger auditLogger,
    NotificationStore notificationStore,
    ILogger<SoftwareProtectionMonitor> logger)
{
    private readonly ProcessObservationTracker _processTracker = new();
    private DateTimeOffset _nextScanAtUtc;
    private bool _developmentSessionWasInactive;

    private static string DevelopmentTestSessionMarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CompanyDlp",
        "Development",
        "active-test-session.json");

    public async Task TickAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _nextScanAtUtc) return;
        _nextScanAtUtc = now.AddSeconds(1);

        var policy = policyStore.Get();
        if (!policy.Enabled || !policy.Software.Enabled) return;

        var activeDevelopmentTestSession = File.Exists(DevelopmentTestSessionMarkerPath);
        if (!DevelopmentTestSessionGate.ShouldMonitor(policy.Runtime.Mode, activeDevelopmentTestSession))
        {
            // Do not block, audit, or notify about installation activity before the user
            // explicitly starts the Development test session. Resetting the tracker makes
            // the first scan after Start establish a fresh baseline with no false alerts.
            _processTracker.Reset();
            if (!_developmentSessionWasInactive)
            {
                logger.LogInformation(
                    "Software protection is idle until Start test session is selected in Development mode.");
                _developmentSessionWasInactive = true;
            }
            return;
        }

        if (_developmentSessionWasInactive)
        {
            logger.LogInformation("Software protection activated for the Development test session.");
            _developmentSessionWasInactive = false;
        }

        var processes = Process.GetProcesses();
        try
        {
            var observation = _processTracker.Observe(processes.Select(process => process.Id));
            if (observation.BaselineEstablished)
            {
                logger.LogInformation(
                    "Software process monitor baseline established with {ProcessCount} existing processes; no startup alerts were generated.",
                    processes.Length);
                return;
            }

            foreach (var process in processes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!observation.NewlyObservedProcessIds.Contains(process.Id)) continue;

                var sessionId = SafeSessionId(process);
                var metadata = TryGetMetadata(process.Id);
                var classification = SoftwareInstallerClassifier.Classify(
                    new SoftwareProcessDescriptor(
                        metadata.ProcessName,
                        metadata.ExecutablePath,
                        metadata.CommandLine,
                        metadata.Publisher,
                        sessionId),
                    policy.Software);

                if (!classification.IsInstaller) continue;

                var context = new ClientContext
                {
                    UserSid = metadata.UserSid,
                    Username = metadata.Username,
                    MachineName = Environment.MachineName,
                    WindowsSessionId = sessionId,
                    ClientName = metadata.ProcessName,
                    ClientVersion = "1.0.0"
                };

                var decision = permissionEvaluator.Evaluate(
                    policy,
                    ActionKeys.SoftwareInstall,
                    context,
                    identityProvider.Get(),
                    now);

                var result = decision.IsAllowed ? "allowed" : "audit-only";
                if (!decision.IsAllowed && policy.Software.EnforcementMode.Equals("Block", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                        result = "blocked";
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(exception, "Could not terminate installer process {ProcessId}", process.Id);
                        result = "block-failed";
                    }
                }
                else if (!decision.IsAllowed && policy.Software.EnforcementMode.Equals("WindowsAppControl", StringComparison.OrdinalIgnoreCase))
                {
                    // WDAC is the enforcement authority. A separate Code Integrity log monitor
                    // records the definitive blocked event (3077/3033) rather than guessing here.
                    result = "delegated-to-wdac";
                }

                var hash = await TryComputeSha256Async(metadata.ExecutablePath, cancellationToken);
                await auditLogger.WriteAsync(new AuditEvent
                {
                    ActionKey = ActionKeys.SoftwareInstall,
                    EventType = decision.IsAllowed
                        ? "InstallationAllowed"
                        : result == "blocked"
                            ? "InstallationBlocked"
                            : "InstallationAttemptDetected",
                    Action = "installer-execution",
                    Method = classification.DetectionReason,
                    Result = result,
                    ReasonCode = decision.IsAllowed
                        ? decision.ReasonCode
                        : result == "blocked"
                            ? "InstallerProcessDeniedByPolicy"
                            : result == "delegated-to-wdac"
                                ? "DelegatedToWindowsAppControl"
                                : decision.ReasonCode,
                    PermissionGrantId = decision.PermissionGrantId,
                    SourceProcessName = metadata.ProcessName,
                    SourceProcessPath = metadata.ExecutablePath,
                    SourceProcessId = process.Id,
                    SourceProcessPublisher = metadata.Publisher,
                    SourceProcessSha256 = hash,
                    ResourceName = Path.GetFileName(metadata.ExecutablePath),
                    ResourceExtension = Path.GetExtension(metadata.ExecutablePath),
                    Details = SanitizeCommandLine(metadata.CommandLine)
                }, context, cancellationToken);

                if (!decision.IsAllowed)
                {
                    notificationStore.Add(
                        "software",
                        result == "blocked" ? "Software installation blocked" : "Software installation detected",
                        $"{metadata.ProcessName} attempted to start an installation operation.",
                        result == "block-failed" ? "Error" : "Warning",
                        result);
                }
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static ProcessMetadata TryGetMetadata(int processId)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT Name, ExecutablePath, CommandLine FROM Win32_Process WHERE ProcessId={processId}");
            foreach (ManagementObject item in searcher.Get())
            {
                var executablePath = item["ExecutablePath"]?.ToString() ?? "";
                var processName = item["Name"]?.ToString() ?? Path.GetFileName(executablePath);
                var commandLine = item["CommandLine"]?.ToString() ?? "";
                var userSid = "";
                var username = "";
                try
                {
                    var sidOutput = new object[] { "" };
                    var sidResult = Convert.ToUInt32(item.InvokeMethod("GetOwnerSid", sidOutput));
                    if (sidResult == 0) userSid = sidOutput[0]?.ToString() ?? "";

                    var ownerOutput = new object[] { "", "" };
                    var ownerResult = Convert.ToUInt32(item.InvokeMethod("GetOwner", ownerOutput));
                    if (ownerResult == 0) username = $"{ownerOutput[1]}\\{ownerOutput[0]}".Trim('\\');
                }
                catch { }

                return new ProcessMetadata(
                    processName,
                    executablePath,
                    commandLine,
                    userSid,
                    username,
                    TryGetPublisher(executablePath));
            }
        }
        catch { }

        return new ProcessMetadata("unknown", "", "", "", "", "");
    }

    private static string TryGetPublisher(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return "";
        try
        {
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            return certificate.Subject;
        }
        catch { return ""; }
    }

    private static async Task<string> TryComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return "";
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 512L * 1024 * 1024) return "";
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            return Convert.ToHexString(hash);
        }
        catch { return ""; }
    }

    private static int SafeSessionId(Process process)
    {
        try { return process.SessionId; } catch { return 0; }
    }

    private static string SanitizeCommandLine(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return "";
        var value = commandLine.Replace('\r', ' ').Replace('\n', ' ');
        foreach (var marker in new[] { "password=", "token=", "secret=", "apikey=", "api-key=" })
        {
            var index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0) value = value[..(index + marker.Length)] + "***";
        }
        return value.Length <= 1000 ? value : value[..1000];
    }

    private sealed record ProcessMetadata(
        string ProcessName,
        string ExecutablePath,
        string CommandLine,
        string UserSid,
        string Username,
        string Publisher);
}
