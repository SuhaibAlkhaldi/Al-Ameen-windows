using System.IO;
using System.Diagnostics;
using CompanyDlp.Contracts;
using CompanyDlp.Desktop.Services;

namespace CompanyDlp.Desktop.Protection;

public sealed class ScreenRecordingProcessBlocker : IDisposable
{
    private readonly ScreenPolicy _policy;
    private readonly PipeClient _pipeClient;
    private readonly Action<string> _statusCallback;
    private readonly Action<string, string> _alertCallback;
    private readonly object _sync = new();
    private readonly HashSet<int> _handledProcessIds = [];
    private readonly List<ProcessWatch> _watchLists;
    private System.Threading.Timer? _timer;
    private int _scanInProgress;
    private bool _disposed;

    public ScreenRecordingProcessBlocker(
        ScreenPolicy policy,
        PipeClient pipeClient,
        Action<string> statusCallback,
        Action<string, string> alertCallback)
    {
        _policy = policy;
        _pipeClient = pipeClient;
        _statusCallback = statusCallback;
        _alertCallback = alertCallback;
        _watchLists =
        [
            new ProcessWatch(
                Enabled: () => _policy.MonitorKnownRecorderProcesses,
                Names: _policy.BlockedRecorderProcessNames,
                ActionKey: ActionKeys.ScreenRecording,
                CapabilityLabel: "screen recording",
                NounSingular: "Screen recording application"),
            new ProcessWatch(
                Enabled: () => _policy.MonitorKnownScreenshotToolProcesses,
                Names: _policy.BlockedScreenshotToolProcessNames,
                ActionKey: ActionKeys.ScreenCapture,
                CapabilityLabel: "screenshot capture",
                NounSingular: "Screenshot tool")
        ];
    }

    public void Start()
    {
        if (_disposed || _timer is not null) return;
        if (!_policy.MonitorKnownRecorderProcesses && !_policy.MonitorKnownScreenshotToolProcesses) return;

        var interval = Math.Clamp(_policy.RecorderPollMilliseconds, 100, 2000);
        _timer = new System.Threading.Timer(
            state => { _ = ScanAsync(); },
            null,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(interval));
    }

    private async Task ScanAsync()
    {
        if (_disposed || Interlocked.Exchange(ref _scanInProgress, 1) == 1) return;

        try
        {
            var activeTargetIds = new HashSet<int>();

            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    string name;
                    try
                    {
                        name = NormalizeProcessName(process.ProcessName);
                    }
                    catch
                    {
                        continue;
                    }

                    if (process.Id == Environment.ProcessId) continue;

                    var watch = _watchLists.FirstOrDefault(w => w.Enabled() && w.Names.Contains(name, StringComparer.OrdinalIgnoreCase));
                    if (watch is null) continue;

                    activeTargetIds.Add(process.Id);

                    lock (_sync)
                    {
                        if (!_handledProcessIds.Add(process.Id)) continue;
                    }

                    var result = "detected";
                    if (_policy.RecorderEnforcementMode.Equals("Block", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            process.Kill(entireProcessTree: true);
                            result = "terminated";
                        }
                        catch
                        {
                            result = "termination-failed";
                        }
                    }

                    var status = result switch
                    {
                        "terminated" => $"{watch.NounSingular} blocked: {name}",
                        "termination-failed" => $"Could not stop {watch.NounSingular.ToLowerInvariant()}: {name}",
                        _ => $"{watch.NounSingular} detected: {name}"
                    };
                    _statusCallback(status);

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
                    _alertCallback(title, message);

                    string processPath;
                    try { processPath = process.MainModule?.FileName ?? ""; } catch { processPath = ""; }
                    await _pipeClient.SendAsync(DlpMessageTypes.Audit, new AuditEvent
                    {
                        ActionKey = watch.ActionKey,
                        EventType = result == "terminated" ? "ScreenProcessBlocked" : "ScreenProcessDetected",
                        Action = "process-detected",
                        Method = watch.ActionKey == ActionKeys.ScreenRecording ? "KnownRecorderProcess" : "KnownScreenshotToolProcess",
                        Result = result,
                        ReasonCode = result == "terminated" ? "ProcessDeniedByPolicy" : result == "termination-failed" ? "ProcessTerminationFailed" : "ProcessAuditOnly",
                        SourceProcessName = name,
                        SourceProcessPath = processPath,
                        SourceProcessId = process.Id,
                        Details = name
                    });
                }
            }

            lock (_sync)
            {
                _handledProcessIds.RemoveWhere(id => !activeTargetIds.Contains(id));
            }
        }
        finally
        {
            Interlocked.Exchange(ref _scanInProgress, 0);
        }
    }

    private static string NormalizeProcessName(string value) =>
        Path.GetFileNameWithoutExtension(value).Trim();

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
        _timer = null;
        lock (_sync) _handledProcessIds.Clear();
    }

    private sealed record ProcessWatch(
        Func<bool> Enabled,
        List<string> Names,
        string ActionKey,
        string CapabilityLabel,
        string NounSingular);
}
