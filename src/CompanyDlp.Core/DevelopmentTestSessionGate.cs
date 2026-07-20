namespace CompanyDlp.Core;

/// <summary>
/// Keeps Development-only service enforcement dormant until the interactive user
/// explicitly starts a test session. Production enforcement is never gated by this marker.
/// </summary>
public static class DevelopmentTestSessionGate
{
    public static bool ShouldMonitor(string? runtimeMode, bool activeTestSessionMarkerExists)
    {
        return !string.Equals(runtimeMode, "Development", StringComparison.OrdinalIgnoreCase)
            || activeTestSessionMarkerExists;
    }
}
