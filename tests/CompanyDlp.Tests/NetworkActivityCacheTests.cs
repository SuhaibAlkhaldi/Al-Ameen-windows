using CompanyDlp.Core;
using CompanyDlp.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CompanyDlp.Tests;

// Covers TryGetSoleRecentlyActiveProcessId - the actual correlation decision DesktopAppProvenanceMonitor
// relies on to decide whether a newly-appeared file can be attributed to a process at all (see
// DlpPolicy.DesktopAppProvenancePolicy's comment for why "exactly one candidate, otherwise
// unattributable" is the deliberate rule, not a simplification). No ETW session involved here at all -
// this class only ever sees plain (processId, timestamp) pairs, which is exactly what makes it testable
// without a real kernel event source.
public sealed class NetworkActivityCacheTests
{
    private static NetworkActivityCache NewCache() =>
        new(new PolicyStore(new MachineDataProtector(), NullLogger<PolicyStore>.Instance));

    [Fact]
    public void TryGetSoleRecentlyActiveProcessId_OneRecentProcess_ReturnsThatProcess()
    {
        var cache = NewCache();
        cache.RecordActivity(1234);

        var result = cache.TryGetSoleRecentlyActiveProcessId(TimeSpan.FromSeconds(30));

        Assert.Equal(1234, result);
    }

    [Fact]
    public void TryGetSoleRecentlyActiveProcessId_NoActivity_ReturnsNull()
    {
        var cache = NewCache();

        var result = cache.TryGetSoleRecentlyActiveProcessId(TimeSpan.FromSeconds(30));

        Assert.Null(result);
    }

    [Fact]
    public void TryGetSoleRecentlyActiveProcessId_TwoDistinctRecentProcesses_IsAmbiguousReturnsNull()
    {
        var cache = NewCache();
        cache.RecordActivity(1234);
        cache.RecordActivity(5678);

        var result = cache.TryGetSoleRecentlyActiveProcessId(TimeSpan.FromSeconds(30));

        Assert.Null(result);
    }

    [Fact]
    public void TryGetSoleRecentlyActiveProcessId_SameProcessRecordedTwice_StillCountsAsOneCandidate()
    {
        var cache = NewCache();
        cache.RecordActivity(1234);
        cache.RecordActivity(1234); // e.g. two packets from the same download

        var result = cache.TryGetSoleRecentlyActiveProcessId(TimeSpan.FromSeconds(30));

        Assert.Equal(1234, result);
    }

    [Fact]
    public void TryGetSoleRecentlyActiveProcessId_ActivityOlderThanWindow_ReturnsNull()
    {
        var cache = NewCache();
        cache.RecordActivity(1234);
        Thread.Sleep(50);

        // A 1-millisecond window means the activity recorded 50ms ago is already outside it.
        var result = cache.TryGetSoleRecentlyActiveProcessId(TimeSpan.FromMilliseconds(1));

        Assert.Null(result);
    }

    [Theory]
    [InlineData(0)] // System Idle
    [InlineData(4)] // System
    public void RecordActivity_SystemPseudoProcesses_AreNeverTracked(int systemPid)
    {
        var cache = NewCache();
        cache.RecordActivity(systemPid);

        var result = cache.TryGetSoleRecentlyActiveProcessId(TimeSpan.FromSeconds(30));

        Assert.Null(result);
    }
}
