namespace CompanyDlp.Core;

/// <summary>
/// Tracks process identifiers across polling cycles. The first observation establishes
/// a baseline and deliberately reports no process as newly started. This prevents an
/// endpoint agent startup from misreporting every already-running process as a new action.
/// </summary>
public sealed class ProcessObservationTracker
{
    private readonly HashSet<int> _seenProcessIds = [];
    private bool _initialized;

    public void Reset()
    {
        _seenProcessIds.Clear();
        _initialized = false;
    }

    public ProcessObservationResult Observe(IEnumerable<int> activeProcessIds)
    {
        ArgumentNullException.ThrowIfNull(activeProcessIds);

        var active = activeProcessIds.Where(id => id > 0).ToHashSet();
        if (!_initialized)
        {
            _seenProcessIds.Clear();
            _seenProcessIds.UnionWith(active);
            _initialized = true;
            return new ProcessObservationResult(true, new HashSet<int>());
        }

        var newlyObserved = active.Where(id => !_seenProcessIds.Contains(id)).ToHashSet();
        _seenProcessIds.IntersectWith(active);
        _seenProcessIds.UnionWith(newlyObserved);

        return new ProcessObservationResult(false, newlyObserved);
    }
}

public sealed record ProcessObservationResult(
    bool BaselineEstablished,
    IReadOnlySet<int> NewlyObservedProcessIds);
