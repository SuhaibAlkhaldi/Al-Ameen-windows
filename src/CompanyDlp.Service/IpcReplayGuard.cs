using System.Collections.Concurrent;

namespace CompanyDlp.Service;

// Extracted from PipeServer (was PipeServer.TryAcceptMessage) purely so this anti-replay logic can be
// unit tested without constructing a full PipeServer and its ~15 dependencies. Behavior is unchanged:
// same MessageId + timestamp-window checks, same cleanup threshold. This is the real per-connection
// replay defense every IPC message goes through - a message with a duplicate MessageId, or one whose
// SentAtUtc falls outside the accepted window (clock skew or a genuinely replayed old message), is
// rejected before it ever reaches request dispatch.
public sealed class IpcReplayGuard(TimeSpan? maximumMessageAge = null)
{
    private static readonly TimeSpan DefaultMaximumMessageAge = TimeSpan.FromMinutes(5);
    private readonly TimeSpan _maximumMessageAge = maximumMessageAge ?? DefaultMaximumMessageAge;
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _recentMessageIds = new();

    public int TrackedMessageCount => _recentMessageIds.Count;

    public bool TryAccept(Guid messageId, DateTimeOffset sentAtUtc, DateTimeOffset now, out string failure)
    {
        failure = "";
        if (messageId == Guid.Empty)
        {
            failure = "IPC messageId is required.";
            return false;
        }

        if (sentAtUtc == default || (now - sentAtUtc).Duration() > _maximumMessageAge)
        {
            failure = "IPC message timestamp is outside the accepted window.";
            return false;
        }

        if (!_recentMessageIds.TryAdd(messageId, now))
        {
            failure = "Duplicate IPC message rejected.";
            return false;
        }

        if (_recentMessageIds.Count > 4096)
        {
            var threshold = now - _maximumMessageAge - TimeSpan.FromMinutes(1);
            foreach (var item in _recentMessageIds)
            {
                if (item.Value < threshold) _recentMessageIds.TryRemove(item.Key, out _);
            }
        }

        return true;
    }
}
