using CompanyDlp.Core;

namespace CompanyDlp.Service;

// Answers "did exactly one (non-excluded) process receive network data recently?" for the file.open-access
// desktop-app channel - see DlpPolicy.DesktopAppProvenancePolicy's comment for the full design and why
// this in-memory, timestamp-only cache (no per-file correlation, no process names, no payload bytes) is
// the shape it is: DesktopAppProvenanceMonitor's ETW side calls RecordActivity(pid) on every inbound
// network event it observes, and its FileSystemWatcher side calls TryGetSoleRecentlyActiveProcessId
// when a watched file stabilizes, to ask "whose network activity, if any single process's, explains
// this file appearing right now?".
//
// Same bounded-size philosophy as UsbSnapshotCache (a sibling cache for the other two provenance
// channels): capped at NetworkActivityCacheMaxTrackedProcesses distinct process IDs, oldest evicted
// first, so a machine with many short-lived processes can't grow this without bound.
public sealed class NetworkActivityCache(PolicyStore policyStore)
{
    private readonly object _sync = new();
    private readonly Dictionary<int, DateTimeOffset> _lastActivityUtcByPid = new();

    // Called from the ETW callback thread (DesktopAppProvenanceMonitor's session.Source.Dynamic.All
    // handler) - must stay cheap and lock-safe, no I/O. pid 0 (System Idle) and 4 (System) are never a
    // real user-mode receiver of application data and are excluded here rather than by every caller.
    public void RecordActivity(int processId)
    {
        if (processId <= 4) return;

        var policy = policyStore.Get().DesktopAppProvenance;
        var now = DateTimeOffset.UtcNow;

        lock (_sync)
        {
            _lastActivityUtcByPid[processId] = now;

            var cap = Math.Max(100, policy.NetworkActivityCacheMaxTrackedProcesses);
            if (_lastActivityUtcByPid.Count <= cap) return;

            // Over cap: first drop anything already stale for this policy's own correlation window
            // (cheap, and the entries most likely to be irrelevant), then - if still over cap on a
            // machine with many genuinely-recent processes - evict the oldest few to get back under it.
            PruneOlderThan(TimeSpan.FromSeconds(Math.Max(1, policy.CorrelationWindowSeconds)));
            if (_lastActivityUtcByPid.Count <= cap) return;

            foreach (var pid in _lastActivityUtcByPid
                         .OrderBy(entry => entry.Value)
                         .Take(_lastActivityUtcByPid.Count - cap)
                         .Select(entry => entry.Key)
                         .ToList())
            {
                _lastActivityUtcByPid.Remove(pid);
            }
        }
    }

    // Returns the one process ID with network activity inside the window ending now, or null when zero
    // or more than one distinct process qualifies - see DlpPolicy.DesktopAppProvenancePolicy's comment
    // for why "more than one" is deliberately treated as unattributable rather than guessed at.
    public int? TryGetSoleRecentlyActiveProcessId(TimeSpan window)
    {
        var cutoff = DateTimeOffset.UtcNow - window;

        lock (_sync)
        {
            PruneOlderThan(window);

            int? sole = null;
            foreach (var entry in _lastActivityUtcByPid)
            {
                if (entry.Value < cutoff) continue;
                if (sole is not null) return null; // a second candidate makes this ambiguous
                sole = entry.Key;
            }
            return sole;
        }
    }

    // Caller already holds _sync. A little slack past the exact window avoids pruning an entry this
    // same call is about to report as a match due to the small delay between RecordActivity and the
    // corresponding TryGetSoleRecentlyActiveProcessId check.
    private void PruneOlderThan(TimeSpan window)
    {
        var cutoff = DateTimeOffset.UtcNow - window - TimeSpan.FromSeconds(30);
        foreach (var pid in _lastActivityUtcByPid
                     .Where(entry => entry.Value < cutoff)
                     .Select(entry => entry.Key)
                     .ToList())
        {
            _lastActivityUtcByPid.Remove(pid);
        }
    }
}
