namespace CompanyDlp.Service;

/// <summary>
/// Wakes the policy synchronization worker when the backend reports that a newer
/// policy is available. The semaphore is intentionally capped at one so repeated
/// heartbeat notifications are coalesced instead of building an unbounded queue.
/// </summary>
public sealed class PolicyRefreshSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void RequestRefresh()
    {
        if (_signal.CurrentCount != 0) return;
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A concurrent heartbeat already requested the refresh.
        }
    }

    public Task<bool> WaitAsync(TimeSpan maximumDelay, CancellationToken cancellationToken) =>
        _signal.WaitAsync(maximumDelay, cancellationToken);
}
