using CompanyDlp.Contracts;
using CompanyDlp.Core;
using CompanyDlp.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CompanyDlp.Tests;

// Tests the real TrustedClock implementation directly (load/save/rollback-detection), not just a
// caller's reaction to a faked ITrustedClock (that's what PermissionEvaluatorClockProtectionTests
// covers). TrustedClock.ResolvePath() has no override seam - it resolves a real, fixed path under
// %LocalAppData% (Development mode) or %ProgramData% (Production), the same hardcoded-path pattern
// AgentIdentityProvider already uses without a seam elsewhere in this test project (see
// FileProtectionCoordinatorClassificationGateTests) - matching that existing convention here rather
// than inventing a new override mechanism. Since that means every test in this class touches the same
// real machine file, each test backs up whatever was already there and restores it in Dispose (xUnit
// creates a fresh instance of this class per [Fact], so this runs before/after every single test).
public sealed class TrustedClockTests : IDisposable
{
    private static readonly TimeSpan RollbackTolerance = TimeSpan.FromMinutes(5);

    private readonly string _statePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CompanyDlp", "Agent", "trusted-clock.bin");

    private readonly byte[]? _backup;

    public TrustedClockTests()
    {
        _backup = File.Exists(_statePath) ? File.ReadAllBytes(_statePath) : null;
        if (File.Exists(_statePath)) File.Delete(_statePath);
    }

    public void Dispose()
    {
        try
        {
            if (_backup is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
                File.WriteAllBytes(_statePath, _backup);
            }
            else if (File.Exists(_statePath))
            {
                File.Delete(_statePath);
            }
        }
        catch { }
    }

    private static TrustedClock CreateClock()
    {
        var policyStore = new PolicyStore(new MachineDataProtector(), NullLogger<PolicyStore>.Instance);
        return new TrustedClock(policyStore, new MachineDataProtector(), NullLogger<TrustedClock>.Instance);
    }

    [Fact]
    public void FirstRun_NoPriorState_HasNoServerTimeAndNoRollback()
    {
        var clock = CreateClock();

        var snapshot = clock.GetSnapshot();

        Assert.False(snapshot.HasServerTime);
        Assert.False(snapshot.ClockRollbackDetected);
        Assert.True((DateTimeOffset.UtcNow - snapshot.UtcNow).Duration() < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ObserveServerTime_ThenSnapshot_HasServerTimeAndNoRollback()
    {
        var clock = CreateClock();
        var serverTime = DateTimeOffset.UtcNow;

        clock.ObserveServerTime(serverTime);
        var snapshot = clock.GetSnapshot();

        Assert.True(snapshot.HasServerTime);
        Assert.False(snapshot.ClockRollbackDetected);
        Assert.True(snapshot.UtcNow >= serverTime);
    }

    // The persisted state is written encrypted (MachineDataProtector/DPAPI) and reloaded lazily on
    // first use - a fresh TrustedClock instance (e.g. after a service restart) must recover the
    // observed server time from disk, not start back at "no server time" every time the process
    // restarts.
    [Fact]
    public void ObserveServerTime_PersistsAcrossInstances()
    {
        var firstClock = CreateClock();
        var serverTime = DateTimeOffset.UtcNow;
        firstClock.ObserveServerTime(serverTime);

        var secondClock = CreateClock();
        var snapshot = secondClock.GetSnapshot();

        Assert.True(snapshot.HasServerTime);
        Assert.False(snapshot.ClockRollbackDetected);
    }

    // ObserveServerTime only ever refuses to move trusted time BACKWARD (that's the protection - a
    // stale or malicious "earlier" server response can't be used to wind trusted time back). It does
    // not refuse a time that's ahead of local wall-clock time. That asymmetry is exactly what makes
    // rollback detection itself testable without touching the OS clock: observe a server time that's
    // safely in the future (allowed - it's not behind anything yet), then read a snapshot immediately.
    // Real wall-clock time (which this test cannot and does not touch) is now legitimately behind the
    // stored trusted reference by design - precisely the condition GetSnapshot()'s rollback check
    // exists to catch, and the same shape of real-world event (local clock reads earlier than the last
    // known-good trusted time) that a genuine OS clock rollback would also produce.
    [Fact]
    public void ObserveFutureServerTime_ThenSnapshot_DetectsRollback()
    {
        var clock = CreateClock();
        var farFutureServerTime = DateTimeOffset.UtcNow.Add(RollbackTolerance).AddHours(1);

        clock.ObserveServerTime(farFutureServerTime);
        var snapshot = clock.GetSnapshot();

        Assert.True(snapshot.HasServerTime);
        Assert.True(snapshot.ClockRollbackDetected);
    }

    [Fact]
    public void ObserveServerTime_RejectsTimeThatWouldMoveTrustedTimeBackward()
    {
        var clock = CreateClock();
        var now = DateTimeOffset.UtcNow;
        clock.ObserveServerTime(now);

        // Attempt to move trusted time backward by well more than the rollback tolerance - must be
        // ignored, not accepted.
        clock.ObserveServerTime(now - RollbackTolerance - TimeSpan.FromMinutes(5));
        var snapshot = clock.GetSnapshot();

        Assert.True(snapshot.HasServerTime);
        Assert.False(snapshot.ClockRollbackDetected);
        // If the rejected, much-earlier observation had been accepted instead, UtcNow here would be
        // far in the past relative to real time; it isn't.
        Assert.True((DateTimeOffset.UtcNow - snapshot.UtcNow).Duration() < TimeSpan.FromSeconds(5));
    }
}
