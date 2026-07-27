using System.Text.Json;
using CompanyDlp.Contracts;
using Xunit;

namespace CompanyDlp.Tests;

public sealed class WatermarkPolicyTests
{
    [Fact]
    public void MissingWatermarkConfiguration_IsEnabledByDefault()
    {
        var policy = new DlpPolicy();

        Assert.True(policy.Watermark.Enabled);
    }

    [Fact]
    public void WatermarkDisablePermission_UsesTheSharedActionKey()
    {
        Assert.Equal("watermark.disable", ActionKeys.WatermarkDisable);
        Assert.Contains(ActionKeys.WatermarkDisable, ActionKeys.All);
    }
    [Fact]
    public void ExplicitWatermarkDisabled_IsPreservedDuringDeserialization()
    {
        var policy = JsonSerializer.Deserialize<DlpPolicy>("{\"watermark\":{\"enabled\":false}}", JsonDefaults.Options);

        Assert.NotNull(policy);
        Assert.False(policy!.Watermark.Enabled);
    }
}