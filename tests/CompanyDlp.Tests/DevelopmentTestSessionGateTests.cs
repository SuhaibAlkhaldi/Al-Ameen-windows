using CompanyDlp.Core;
using Xunit;

namespace CompanyDlp.Tests;

public sealed class DevelopmentTestSessionGateTests
{
    [Fact]
    public void DevelopmentWithoutActiveMarker_DoesNotMonitor()
    {
        Assert.False(DevelopmentTestSessionGate.ShouldMonitor("Development", false));
    }

    [Fact]
    public void DevelopmentWithActiveMarker_Monitors()
    {
        Assert.True(DevelopmentTestSessionGate.ShouldMonitor("Development", true));
    }

    [Fact]
    public void ProductionAlwaysMonitors()
    {
        Assert.True(DevelopmentTestSessionGate.ShouldMonitor("Production", false));
    }
}
