using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using CompanyDlp.Contracts;
using CompanyDlp.Core;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace CompanyDlp.Service;

// Third file.open-access provenance channel (FileProvenanceChannels.DesktopApp) - detects a file that
// landed via some desktop application (Outlook, Teams, or any other; deliberately not app-specific)
// receiving it from the network and saving it into a watched folder, the way BrowserPolicy/UsbPolicy
// already cover a browser download and a USB copy respectively. See DlpPolicy.DesktopAppProvenancePolicy's
// comment for the full design rationale and why this correlates rather than exactly attributes: a
// Phase-0 spike (confirmed live 2026-09-09, run elevated against this exact agent) found that
// Microsoft-Windows-Kernel-File does not reliably fire through a normal, isolated ETW session on this
// machine - zero file events were observed across both a positive test (download + save into a watched
// folder) and a negative control, despite the session enabling successfully with no error. The only
// alternative Windows offers for that signal - the classic single-consumer-per-machine "NT Kernel
// Logger" - risks starving any other security/monitoring tool on the device that also needs it,
// unacceptable for a background DLP agent to gamble with. So detection instead pairs two independent,
// safe-to-run-alongside-anything-else signals:
//   1. A FileSystemWatcher per FileClassification.WatchedFolders folder - tells us WHEN a file appeared
//      and, after a short exclusive-open retry loop, that its writer has closed it. No process
//      attribution at all; FileSystemWatcher simply doesn't carry that information.
//   2. Microsoft-Windows-Kernel-Network via a normal, isolated TraceEventSession - the same spike
//      confirmed this DOES fire reliably as a modern, manifest-based provider, with a real owning
//      process ID in each event's "PID" payload field. Every inbound-data event's PID is recorded into
//      NetworkActivityCache.
// A file is attributed to this channel only when NetworkActivityCache reports EXACTLY ONE non-excluded
// process with network activity inside CorrelationWindowSeconds of the file appearing
// (TryGetSoleRecentlyActiveProcessId) - zero or multiple candidates is left unattributable (the file
// stays SelfCreated, the default) rather than guessed at. This is a deliberate, accepted tradeoff:
// missing a detection here is far cheaper than wrongly auto-encrypting a user's own file, and unlike
// BrowserDownload/Usb this channel can never offer an exact per-file guarantee without the kernel-level
// signal the spike ruled out.
//
// Same dedicated-BackgroundService shape as PrintProtectionMonitor (not ticked from DlpWorker's shared
// loop): the ETW session's Source.Process() call blocks synchronously for as long as the session runs,
// so it gets its own long-lived background Thread rather than occupying a thread-pool thread or sharing
// a cadence meant for unrelated, much cheaper polling. Detection callbacks (both the ETW handler and the
// FileSystemWatcher handlers) do no I/O themselves - they only enqueue and signal, matching
// PrintProtectionMonitor's own split between a fast detection thread and ExecuteAsync's real work.
public sealed class DesktopAppProvenanceMonitor(
    PolicyStore policyStore,
    NetworkActivityCache networkActivityCache,
    FileProvenanceStore provenanceStore,
    InteractiveUserContextProvider interactiveUserContextProvider,
    ILogger<DesktopAppProvenanceMonitor> logger) : BackgroundService
{
    private const string SessionName = "CompanyDlp-DesktopAppProvenance";

    private readonly ConcurrentQueue<string> _pendingPaths = new();

    // Released by both the ETW callback thread and every FileSystemWatcher callback thread the instant
    // something is detected - SemaphoreSlim.Release() does no I/O and is safe from either.
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private readonly List<FileSystemWatcher> _watchers = [];

    private TraceEventSession? _etwSession;
    private bool _startAttempted;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var policy = policyStore.Get();
            if (!policy.Enabled || !policy.DesktopAppProvenance.Enabled)
            {
                Stop();
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                continue;
            }

            EnsureStarted(policy.FileClassification.WatchedFolders);

            try
            {
                // The 2-second timeout just rechecks a policy flip (Enabled -> false) or watched-folder
                // change promptly even with nothing detected - the semaphore is what actually drives
                // fast reaction to a real detection.
                await _signal.WaitAsync(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            var current = policyStore.Get().DesktopAppProvenance;
            var window = TimeSpan.FromSeconds(Math.Max(1, current.CorrelationWindowSeconds));
            while (_pendingPaths.TryDequeue(out var path))
            {
                TryAttribute(path, current, window);
            }
        }

        Stop();
    }

    private void EnsureStarted(IReadOnlyList<string> watchedFolders)
    {
        if (_startAttempted) return;
        _startAttempted = true;

        new Thread(RunEtwSession) { IsBackground = true, Name = "CompanyDlp-DesktopAppProvenance-ETW" }.Start();
        StartFileWatchers(watchedFolders);
    }

    // Runs entirely on its own thread for the monitor's whole lifetime - TraceEventSession.Source.Process()
    // blocks synchronously until Stop() is called on the session, by design (it's pumping the real-time
    // ETW buffer). Never throws out to the caller: a session that fails to start (no Administrator
    // rights, provider unavailable, ...) just means this channel silently detects nothing over the
    // network side - soft-fail, same philosophy as every other monitor in this file.
    private void RunEtwSession()
    {
        try
        {
            // Clean up a leftover session from a previous crashed run, if any - same guard the Phase-0
            // spike used.
            if (TraceEventSession.GetActiveSessionNames().Contains(SessionName))
            {
                using var stale = new TraceEventSession(SessionName) { StopOnDispose = true };
                stale.Stop();
            }

            using var session = new TraceEventSession(SessionName) { StopOnDispose = true };
            _etwSession = session;

            session.EnableProvider("Microsoft-Windows-Kernel-Network", TraceEventLevel.Informational, ulong.MaxValue);
            session.Source.Dynamic.All += OnNetworkEvent;
            session.Source.Process(); // blocks until Stop() below calls session.Stop()
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Could not start the desktop-app-provenance network session (this agent may not be running elevated); this channel will detect nothing until the service restarts.");
        }
        finally
        {
            _etwSession = null;
        }
    }

    // Runs on the ETW real-time delivery thread - must stay cheap, no I/O. See NetworkActivityCache's
    // class comment for why only a process ID and timestamp are ever recorded, nothing else.
    private void OnNetworkEvent(TraceEvent data)
    {
        try
        {
            if (data.ProviderName != "Microsoft-Windows-Kernel-Network") return;

            // The event's own ProcessID is frequently the System process (pid 4) for TCPIP work
            // performed on a connection's behalf - confirmed live in the Phase-0 spike. The payload's
            // own "PID" field is the real owning process on the event shapes that carry it; fall back
            // to ProcessID when that field is absent.
            int pid;
            try
            {
                pid = data.PayloadByName("PID") is { } value ? Convert.ToInt32(value) : data.ProcessID;
            }
            catch
            {
                pid = data.ProcessID;
            }

            networkActivityCache.RecordActivity(pid);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not process a network event for desktop-app provenance.");
        }
    }

    private void StartFileWatchers(IReadOnlyList<string> watchedFolders)
    {
        var context = interactiveUserContextProvider.GetActiveConsoleUser();
        var interactiveProfilePath = WatchedFolderPathResolver.ResolveInteractiveUserProfilePath(context.UserSid, logger);

        foreach (var folder in watchedFolders)
        {
            var expanded = WatchedFolderPathResolver.ExpandWatchedFolderPath(folder, interactiveProfilePath);
            if (!Directory.Exists(expanded)) continue;

            try
            {
                var watcher = new FileSystemWatcher(expanded)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
                };
                watcher.Created += OnFileEvent;
                watcher.Changed += OnFileEvent;
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not watch folder {Folder} for desktop-app provenance.", expanded);
            }
        }
    }

    // Runs on a FileSystemWatcher callback thread - deliberately does no I/O here (no stabilization
    // wait, no hashing), matching PrintProtectionMonitor's own "detection thread just enqueues" split.
    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        _pendingPaths.Enqueue(e.FullPath);
        _signal.Release();
    }

    // Runs on ExecuteAsync's own async context, one path at a time - the actual I/O (stabilization
    // wait, hashing, correlation lookup, MarkReceived) happens here, never on the FileSystemWatcher
    // callback thread.
    private void TryAttribute(string path, DesktopAppProvenancePolicy policy, TimeSpan window)
    {
        try
        {
            if (!DocumentTextExtractor.IsSupported(Path.GetExtension(path))) return;
            if (!WaitUntilStable(path)) return; // still being written, or already gone - skip, nothing lost

            var pid = networkActivityCache.TryGetSoleRecentlyActiveProcessId(window);
            if (pid is null) return; // zero or multiple candidates - unattributable by design, see class comment

            string processName;
            try
            {
                processName = Process.GetProcessById(pid.Value).ProcessName;
            }
            catch
            {
                return; // process already exited - nothing left to attribute this file to
            }

            if (policy.ExcludedProcessNames.Any(excluded => excluded.Equals(processName, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            var hash = ComputeHash(path);
            if (hash is null) return;

            provenanceStore.MarkReceived(hash, FileProvenanceChannels.DesktopApp);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not evaluate desktop-app provenance for {Path}.", path);
        }
    }

    // A brief exclusive-open retry loop is how this class learns a file's writer has actually closed
    // it - FileSystemWatcher itself has no "closed" event. Short enough not to meaningfully delay
    // detection, long enough to cover typical small-attachment write latency (~1 second worst case).
    private static bool WaitUntilStable(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                return true;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            catch (IOException) { Thread.Sleep(200); }
            catch (UnauthorizedAccessException) { Thread.Sleep(200); }
        }
        return false;
    }

    private static string? ComputeHash(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    private void Stop()
    {
        if (!_startAttempted) return;

        try { _etwSession?.Stop(); } catch { /* best-effort - the session thread's own finally clears it either way */ }

        foreach (var watcher in _watchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Created -= OnFileEvent;
                watcher.Changed -= OnFileEvent;
                watcher.Dispose();
            }
            catch { }
        }
        _watchers.Clear();

        _startAttempted = false;
    }
}
