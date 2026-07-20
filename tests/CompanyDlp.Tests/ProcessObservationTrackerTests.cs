using Xunit;
using CompanyDlp.Core;

namespace CompanyDlp.Tests;

public sealed class ProcessObservationTrackerTests
{
    [Fact]
    public void FirstObservation_EstablishesBaselineWithoutReportingExistingProcesses()
    {
        var tracker = new ProcessObservationTracker();

        var result = tracker.Observe([10, 20, 30]);

        Assert.True(result.BaselineEstablished);
        Assert.Empty(result.NewlyObservedProcessIds);
    }

    [Fact]
    public void LaterObservation_ReportsOnlyNewProcesses()
    {
        var tracker = new ProcessObservationTracker();
        tracker.Observe([10, 20, 30]);

        var result = tracker.Observe([20, 30, 40]);

        Assert.False(result.BaselineEstablished);
        Assert.Single(result.NewlyObservedProcessIds);
        Assert.Contains(40, result.NewlyObservedProcessIds);
    }

    [Fact]
    public void Reset_CausesNextObservationToEstablishFreshBaseline()
    {
        var tracker = new ProcessObservationTracker();
        tracker.Observe([10, 20]);
        tracker.Observe([10, 20, 30]);

        tracker.Reset();
        var result = tracker.Observe([10, 20, 30, 40]);

        Assert.True(result.BaselineEstablished);
        Assert.Empty(result.NewlyObservedProcessIds);
    }
}
