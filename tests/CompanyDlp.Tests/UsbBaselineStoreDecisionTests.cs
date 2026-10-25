using CompanyDlp.Contracts;
using CompanyDlp.Service;
using Xunit;

namespace CompanyDlp.Tests;

// Tests UsbBaselineStore's pure decision logic (made `internal` for exactly this purpose - see
// UsbProtectionMonitorDecisionTests' own header comment for the same pattern) directly: given a
// baseline entry/bundle/timestamp, what does the store decide. Deliberately does NOT cover GetOrCreate
// itself - that method does real file IO and reads the real PolicyStore, and a test that exercised it
// end to end would depend on disk state and isn't appropriate for a unit test. These methods take
// explicit inputs instead (JSON text, entries, timestamps), so no real clock or file is ever touched.
public sealed class UsbBaselineStoreDecisionTests
{
    private static UsbDeviceBundleInfo CreateBundle(
        string rootInstanceId = "USB\\ROOT\\001",
        string vendorId = "1234",
        string productId = "5678") => new()
    {
        RootInstanceId = rootInstanceId,
        VendorId = vendorId,
        ProductId = productId
    };

    // --- ParseBaselineJson ---

    [Fact]
    public void ParseBaselineJson_LegacyStringArray_ParsesAsRootInstanceIdOnlyEntries()
    {
        var entries = UsbBaselineStore.ParseBaselineJson("[\"USB\\\\ROOT\\\\1\",\"USB\\\\ROOT\\\\2\"]");

        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry =>
        {
            Assert.Equal("", entry.VendorId);
            Assert.Equal("", entry.ProductId);
            Assert.False(string.IsNullOrWhiteSpace(entry.RootInstanceId));
        });
    }

    [Fact]
    public void ParseBaselineJson_NewRecordArray_ParsesAllFields()
    {
        var entries = UsbBaselineStore.ParseBaselineJson(
            "[{\"RootInstanceId\":\"USB\\\\ROOT\\\\1\",\"VendorId\":\"1234\",\"ProductId\":\"5678\"}]");

        Assert.Single(entries);
        Assert.Equal("USB\\ROOT\\1", entries[0].RootInstanceId);
        Assert.Equal("1234", entries[0].VendorId);
        Assert.Equal("5678", entries[0].ProductId);
    }

    [Fact]
    public void ParseBaselineJson_EmptyArray_ReturnsEmpty()
    {
        Assert.Empty(UsbBaselineStore.ParseBaselineJson("[]"));
    }

    [Fact]
    public void ParseBaselineJson_LegacyArrayWithBlankEntries_SkipsBlanks()
    {
        var entries = UsbBaselineStore.ParseBaselineJson("[\"USB\\\\ROOT\\\\1\",\"\",\"   \"]");

        Assert.Single(entries);
        Assert.Equal("USB\\ROOT\\1", entries[0].RootInstanceId);
    }

    // --- MatchesEntry / IsTrusted ---

    [Fact]
    public void MatchesEntry_RootInstanceIdMatch_ReturnsTrue()
    {
        var entry = new UsbBaselineEntry("USB\\ROOT\\1", "", "");
        var bundle = CreateBundle(rootInstanceId: "USB\\ROOT\\1", vendorId: "9999", productId: "9999");

        Assert.True(UsbBaselineStore.MatchesEntry(entry, bundle));
    }

    // Direct regression test for the Windows-Update/driver-reinstall drift root cause: the entry's
    // RootInstanceId no longer matches the bundle's (the OS reassigned its enumeration path), but the
    // physical hardware identity (VID:PID) is unchanged, so trust must still be recognized.
    [Fact]
    public void MatchesEntry_VidPidMatch_DifferentRootInstanceId_ReturnsTrue()
    {
        var entry = new UsbBaselineEntry("USB\\ROOT\\OLD", "1234", "5678");
        var bundle = CreateBundle(rootInstanceId: "USB\\ROOT\\NEW", vendorId: "1234", productId: "5678");

        Assert.True(UsbBaselineStore.MatchesEntry(entry, bundle));
    }

    [Fact]
    public void MatchesEntry_NoMatchOnEitherLeg_ReturnsFalse()
    {
        var entry = new UsbBaselineEntry("USB\\ROOT\\OLD", "1111", "2222");
        var bundle = CreateBundle(rootInstanceId: "USB\\ROOT\\NEW", vendorId: "3333", productId: "4444");

        Assert.False(UsbBaselineStore.MatchesEntry(entry, bundle));
    }

    [Fact]
    public void MatchesEntry_EmptyVendorOrProductId_DoesNotFalseMatchOnBlank()
    {
        var entry = new UsbBaselineEntry("USB\\ROOT\\OLD", "", "");
        var bundle = CreateBundle(rootInstanceId: "USB\\ROOT\\NEW", vendorId: "", productId: "");

        Assert.False(UsbBaselineStore.MatchesEntry(entry, bundle));
    }

    [Fact]
    public void IsTrusted_AnyEntryMatches_ReturnsTrue()
    {
        var entries = new List<UsbBaselineEntry>
        {
            new("USB\\ROOT\\OTHER", "0000", "0000"),
            new("USB\\ROOT\\1", "1234", "5678")
        };
        var bundle = CreateBundle(rootInstanceId: "USB\\ROOT\\1");

        Assert.True(UsbBaselineStore.IsTrusted(entries, bundle));
    }

    [Fact]
    public void IsTrusted_EmptyEntries_ReturnsFalse()
    {
        Assert.False(UsbBaselineStore.IsTrusted([], CreateBundle()));
    }

    // --- IsSettleWindowElapsed ---

    [Fact]
    public void IsSettleWindowElapsed_BeforeWindow_ReturnsFalse()
    {
        var first = DateTimeOffset.UtcNow;
        var now = first.AddSeconds(30);

        Assert.False(UsbBaselineStore.IsSettleWindowElapsed(first, now, TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void IsSettleWindowElapsed_ExactlyAtWindow_ReturnsTrue()
    {
        var first = DateTimeOffset.UtcNow;
        var now = first.AddSeconds(60);

        Assert.True(UsbBaselineStore.IsSettleWindowElapsed(first, now, TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void IsSettleWindowElapsed_AfterWindow_ReturnsTrue()
    {
        var first = DateTimeOffset.UtcNow;
        var now = first.AddSeconds(90);

        Assert.True(UsbBaselineStore.IsSettleWindowElapsed(first, now, TimeSpan.FromSeconds(60)));
    }
}
