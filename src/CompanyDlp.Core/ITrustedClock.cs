namespace CompanyDlp.Core;

public sealed record TrustedClockSnapshot(DateTimeOffset UtcNow, bool HasServerTime, bool ClockRollbackDetected);

public interface ITrustedClock
{
    TrustedClockSnapshot GetSnapshot();
}
