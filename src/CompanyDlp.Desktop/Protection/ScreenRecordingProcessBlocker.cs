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
    }

    public void Start()
    {
        if (_disposed || !_policy.MonitorKnownRecorderProcesses || _timer is not null) return;

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
            var targets = _policy.BlockedProcessNames
                .Select(NormalizeProcessName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

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

                    if (!targets.Contains(name) || process.Id == Environment.ProcessId) continue;
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
                        "terminated" => $"Screen recorder blocked: {name}",
                        "termination-failed" => $"Could not stop screen recorder: {name}",
                        _ => $"Screen recorder detected: {name}"
                    };
                    _statusCallback(status);

                    var title = result == "termination-failed"
                        ? "Screen recording application could not be stopped"
                        : "Screen recording application blocked";
                    var message = result switch
                    {
                        "terminated" => $"{name} was closed because screen recording is prohibited by company policy.",
                        "termination-failed" => $"{name} was detected, but Company DLP could not close it. Contact IT.",
                        _ => $"{name} was detected while screen recording protection is enabled."
                    };
                    _alertCallback(title, message);

                    string processPath;
                    try { processPath = process.MainModule?.FileName ?? ""; } catch { processPath = ""; }
                    await _pipeClient.SendAsync(DlpMessageTypes.Audit, new AuditEvent
                    {
                        ActionKey = ActionKeys.ScreenRecording,
                        EventType = result == "terminated" ? "ScreenRecordingBlocked" : "ScreenRecordingApplicationDetected",
                        Action = "process-detected",
                        Method = "KnownRecorderProcess",
                        Result = result,
                        ReasonCode = result == "terminated" ? "RecorderProcessDeniedByPolicy" : result == "termination-failed" ? "RecorderTerminationFailed" : "RecorderAuditOnly",
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
}
