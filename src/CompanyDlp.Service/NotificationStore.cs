using System.Collections.Concurrent;
using CompanyDlp.Contracts;

namespace CompanyDlp.Service;

public sealed class NotificationStore
{
    private const int MaximumRetained = 200;
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(3);
    private readonly ConcurrentQueue<UserNotification> _notifications = new();
    private readonly Dictionary<string, (DateTimeOffset Timestamp, UserNotification Notification)> _recent =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private long _nextId;

    public UserNotification Add(
        string category,
        string title,
        string message,
        string severity = "Warning",
        string action = "blocked")
    {
        var now = DateTimeOffset.UtcNow;
        var key = string.Join('|', category, action, title, message, severity);

        lock (_gate)
        {
            if (_recent.TryGetValue(key, out var recent)
                && now - recent.Timestamp < DuplicateWindow)
            {
                return recent.Notification;
            }

            var notification = new UserNotification
            {
                Id = Interlocked.Increment(ref _nextId),
                OccurredAtUtc = now,
                Category = category,
                Title = title,
                Message = message,
                Severity = severity,
                Action = action
            };

            _notifications.Enqueue(notification);
            _recent[key] = (now, notification);

            foreach (var expiredKey in _recent
                         .Where(item => now - item.Value.Timestamp > TimeSpan.FromMinutes(1))
                         .Select(item => item.Key)
                         .ToList())
            {
                _recent.Remove(expiredKey);
            }

            while (_notifications.Count > MaximumRetained)
            {
                _notifications.TryDequeue(out _);
            }

            return notification;
        }
    }

    public IReadOnlyList<UserNotification> GetAfter(long afterId) =>
        _notifications
            .Where(item => item.Id > afterId)
            .OrderBy(item => item.Id)
            .ToList();
}
