using CompanyDlp.Contracts;
using CompanyDlp.Service;
using Xunit;

namespace CompanyDlp.Tests;

// Tests UsbProtectionMonitor's actual device-gating decision rules (made `internal` for exactly this
// purpose - see that file's header comment) directly: given a device/policy description, what does the
// monitor decide. Deliberately does NOT attempt to test TickAsync's own orchestration end to end -
// UsbDeviceInventory enumerates real hardware via WMI and UsbDeviceController shells out to
// pnputil.exe /disable-device, and a test that constructed real instances of those would depend on
// whatever's actually plugged into the machine running the test and could disable a real device if the
// simulated policy ever reached the Block branch. Neither is appropriate for a unit test; the
// two-tick arrival-settling state machine and the audit/notification dispatch inside TickAsync are
// consequently not covered here.
public sealed class UsbProtectionMonitorDecisionTests
{
    private static UsbDeviceBundleInfo CreateBundle(
        string rootInstanceId = "USB\\ROOT\\001",
        List<string>? classes = null,
        string hardwareId = "",
        string vendorId = "1234",
        string productId = "5678",
        string serialNumber = "",
        bool hasKeyboardOrMouse = false,
        bool hasForbiddenFunction = false) => new()
    {
        RootInstanceId = rootInstanceId,
        Classes = classes ?? [],
        HardwareId = hardwareId,
        VendorId = vendorId,
        ProductId = productId,
        SerialNumber = serialNumber,
        HasKeyboardOrMouse = hasKeyboardOrMouse,
        HasForbiddenFunction = hasForbiddenFunction
    };

    // --- ResolveGatingActionKey ---

    [Fact]
    public void ResolveGatingActionKey_DiskDriveClass_MapsToUsbStorage()
    {
        var bundle = CreateBundle(classes: ["USB", "DiskDrive"]);
        Assert.Equal(ActionKeys.UsbStorage, UsbProtectionMonitor.ResolveGatingActionKey(bundle));
    }

    [Fact]
    public void ResolveGatingActionKey_WpdClass_MapsToUsbMobileDevice()
    {
        var bundle = CreateBundle(classes: ["WPD"]);
        Assert.Equal(ActionKeys.UsbMobileDevice, UsbProtectionMonitor.ResolveGatingActionKey(bundle));
    }

    [Fact]
    public void ResolveGatingActionKey_OtherClass_FallsBackToGeneralConnect()
    {
        var bundle = CreateBundle(classes: ["HIDClass"]);
        Assert.Equal(ActionKeys.UsbDeviceConnect, UsbProtectionMonitor.ResolveGatingActionKey(bundle));
    }

    [Fact]
    public void ResolveGatingActionKey_IsCaseInsensitive()
    {
        var bundle = CreateBundle(classes: ["diskdrive"]);
        Assert.Equal(ActionKeys.UsbStorage, UsbProtectionMonitor.ResolveGatingActionKey(bundle));
    }

    // --- IsExplicitlyApproved ---

    [Fact]
    public void IsExplicitlyApproved_MatchingHardwareId_ReturnsTrue()
    {
        var policy = new UsbPolicy { ApprovedHardwareIds = ["USB\\VID_1234&PID_5678"] };
        var bundle = CreateBundle(hardwareId: "USB\\VID_1234&PID_5678");
        Assert.True(UsbProtectionMonitor.IsExplicitlyApproved(policy, bundle));
    }

    [Fact]
    public void IsExplicitlyApproved_MatchingVidPidColonForm_ReturnsTrue()
    {
        var policy = new UsbPolicy { ApprovedVidPid = ["1234:5678"] };
        var bundle = CreateBundle(vendorId: "1234", productId: "5678");
        Assert.True(UsbProtectionMonitor.IsExplicitlyApproved(policy, bundle));
    }

    [Fact]
    public void IsExplicitlyApproved_MatchingVidPidWindowsForm_ReturnsTrue()
    {
        var policy = new UsbPolicy { ApprovedVidPid = ["VID_1234&PID_5678"] };
        var bundle = CreateBundle(vendorId: "1234", productId: "5678");
        Assert.True(UsbProtectionMonitor.IsExplicitlyApproved(policy, bundle));
    }

    [Fact]
    public void IsExplicitlyApproved_MatchingSerialNumber_ReturnsTrue()
    {
        var policy = new UsbPolicy { ApprovedSerialNumbers = ["ABC123"] };
        var bundle = CreateBundle(serialNumber: "abc123");
        Assert.True(UsbProtectionMonitor.IsExplicitlyApproved(policy, bundle));
    }

    [Fact]
    public void IsExplicitlyApproved_NoMatch_ReturnsFalse()
    {
        var policy = new UsbPolicy { ApprovedHardwareIds = ["USB\\VID_9999&PID_9999"] };
        var bundle = CreateBundle(hardwareId: "USB\\VID_1234&PID_5678", vendorId: "1234", productId: "5678");
        Assert.False(UsbProtectionMonitor.IsExplicitlyApproved(policy, bundle));
    }

    [Fact]
    public void IsExplicitlyApproved_EmptyAllowlist_ReturnsFalse()
    {
        var policy = new UsbPolicy();
        var bundle = CreateBundle(hardwareId: "USB\\VID_1234&PID_5678");
        Assert.False(UsbProtectionMonitor.IsExplicitlyApproved(policy, bundle));
    }

    // --- IsSafeHid ---

    [Fact]
    public void IsSafeHid_KeyboardOrMouse_PolicyAllows_NoForbiddenFunction_ReturnsTrue()
    {
        var policy = new UsbPolicy { AllowAnyKeyboardOrMouse = true, DenyCompositeDevicesWithForbiddenFunctions = true };
        var bundle = CreateBundle(hasKeyboardOrMouse: true, hasForbiddenFunction: false);
        Assert.True(UsbProtectionMonitor.IsSafeHid(policy, bundle));
    }

    [Fact]
    public void IsSafeHid_PolicyDisallowsAnyKeyboardOrMouse_ReturnsFalse()
    {
        var policy = new UsbPolicy { AllowAnyKeyboardOrMouse = false };
        var bundle = CreateBundle(hasKeyboardOrMouse: true, hasForbiddenFunction: false);
        Assert.False(UsbProtectionMonitor.IsSafeHid(policy, bundle));
    }

    [Fact]
    public void IsSafeHid_NotAKeyboardOrMouse_ReturnsFalse()
    {
        var policy = new UsbPolicy { AllowAnyKeyboardOrMouse = true };
        var bundle = CreateBundle(hasKeyboardOrMouse: false);
        Assert.False(UsbProtectionMonitor.IsSafeHid(policy, bundle));
    }

    // A composite device that's ALSO a keyboard/mouse but has an extra forbidden function (e.g. a
    // keyboard with a hidden mass-storage partition) must not be treated as a safe HID device when the
    // policy is configured to deny that composite pattern - this is exactly the disguised-storage-
    // device attack this check exists to catch.
    [Fact]
    public void IsSafeHid_CompositeWithForbiddenFunction_PolicyDenies_ReturnsFalse()
    {
        var policy = new UsbPolicy { AllowAnyKeyboardOrMouse = true, DenyCompositeDevicesWithForbiddenFunctions = true };
        var bundle = CreateBundle(hasKeyboardOrMouse: true, hasForbiddenFunction: true);
        Assert.False(UsbProtectionMonitor.IsSafeHid(policy, bundle));
    }

    [Fact]
    public void IsSafeHid_CompositeWithForbiddenFunction_PolicyDoesNotDeny_ReturnsTrue()
    {
        var policy = new UsbPolicy { AllowAnyKeyboardOrMouse = true, DenyCompositeDevicesWithForbiddenFunctions = false };
        var bundle = CreateBundle(hasKeyboardOrMouse: true, hasForbiddenFunction: true);
        Assert.True(UsbProtectionMonitor.IsSafeHid(policy, bundle));
    }

    // --- ResolveIsAllowed (the overall per-device combining decision) ---

    [Theory]
    [InlineData(true, false, false, false, true)]
    [InlineData(false, true, false, false, true)]
    [InlineData(false, false, true, false, true)]
    [InlineData(false, false, false, true, true)]
    [InlineData(false, false, false, false, false)]
    public void ResolveIsAllowed_AnyTruePath_AllowsOverall(
        bool safeHid, bool explicitlyApproved, bool isTrustedBaseline, bool grantAllowed, bool expected)
    {
        Assert.Equal(expected, UsbProtectionMonitor.ResolveIsAllowed(safeHid, explicitlyApproved, isTrustedBaseline, grantAllowed));
    }

    // --- Matches ---

    [Fact]
    public void Matches_TrimsAndIgnoresCase()
    {
        Assert.True(UsbProtectionMonitor.Matches("  ABC123  ", "abc123"));
    }

    [Fact]
    public void Matches_EmptyExpected_NeverMatches()
    {
        Assert.False(UsbProtectionMonitor.Matches("", "anything"));
        Assert.False(UsbProtectionMonitor.Matches("   ", "anything"));
    }

    // --- MaskSerial ---

    [Fact]
    public void MaskSerial_ShortSerial_FullyMasked()
    {
        Assert.Equal("****", UsbProtectionMonitor.MaskSerial("1234"));
    }

    [Fact]
    public void MaskSerial_LongerSerial_KeepsLastFourDigitsVisible()
    {
        var masked = UsbProtectionMonitor.MaskSerial("SN00112233445566");
        Assert.EndsWith("5566", masked);
        Assert.DoesNotContain("0011", masked);
    }

    [Fact]
    public void MaskSerial_Empty_ReturnsEmpty()
    {
        Assert.Equal("", UsbProtectionMonitor.MaskSerial(""));
    }

    // --- ResolveAllowMethod / ResolveAllowReason ---

    [Fact]
    public void ResolveAllowMethod_GrantAllowed_ReturnsPermissionSource()
    {
        var decision = new PermissionDecision { IsAllowed = true, PermissionSource = PermissionSources.TemporaryGrant };
        var result = UsbProtectionMonitor.ResolveAllowMethod(new UsbPolicy(), CreateBundle(), decision);
        Assert.Equal(PermissionSources.TemporaryGrant, result);
    }

    [Fact]
    public void ResolveAllowMethod_ExplicitAllowlist_ReturnsDeviceAllowlist()
    {
        var policy = new UsbPolicy { ApprovedHardwareIds = ["HWID-1"] };
        var bundle = CreateBundle(hardwareId: "HWID-1");
        var decision = new PermissionDecision { IsAllowed = false };
        Assert.Equal("DeviceAllowlist", UsbProtectionMonitor.ResolveAllowMethod(policy, bundle, decision));
    }

    [Fact]
    public void ResolveAllowMethod_TrustedBaseline_ReturnsTrustedBaseline()
    {
        var bundle = CreateBundle();
        bundle.IsTrustedBaseline = true;
        var decision = new PermissionDecision { IsAllowed = false };
        Assert.Equal("TrustedBaseline", UsbProtectionMonitor.ResolveAllowMethod(new UsbPolicy(), bundle, decision));
    }

    [Fact]
    public void ResolveAllowMethod_FallsBackToSafeHid()
    {
        var decision = new PermissionDecision { IsAllowed = false };
        Assert.Equal("SafeHid", UsbProtectionMonitor.ResolveAllowMethod(new UsbPolicy(), CreateBundle(), decision));
    }
}
