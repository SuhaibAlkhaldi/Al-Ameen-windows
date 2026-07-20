using System.Runtime.InteropServices;
using System.Text.Json;
using CompanyDlp.Contracts;

namespace CompanyDlp.Service;

public sealed class UsbProtectionMonitor(
    PolicyStore policyStore,
    RuntimeOverrideStore runtimeOverrides,
    AgentIdentityProvider identityProvider,
    InteractiveUserContextProvider interactiveUserContextProvider,
    PermissionEvaluator permissionEvaluator,
    UsbDeviceInventory inventory,
    UsbBaselineStore baselineStore,
    UsbDeviceController controller,
    AuditLogger auditLogger,
    NotificationStore notificationStore,
    ILogger<UsbProtectionMonitor> logger)
{
    private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<UsbDeviceBundleInfo> LastSnapshot { get; private set; } = [];

    public async Task TickAsync(bool initial, CancellationToken cancellationToken)
    {
        var policy = policyStore.Get();
        if (!policy.Enabled || !policy.Usb.Enabled || !OperatingSystem.IsWindows()) return;

        var bundles = inventory.GetPresentBundles();
        var baseline = policy.Usb.TrustDevicesPresentAtFirstRun
            ? baselineStore.GetOrCreate(bundles)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activeContext = interactiveUserContextProvider.GetActiveConsoleUser();
        var identity = identityProvider.Get();

        foreach (var bundle in bundles)
        {
            bundle.IsTrustedBaseline = baseline.Contains(bundle.RootInstanceId);
            var explicitlyApproved = IsExplicitlyApproved(policy.Usb, bundle);
            var safeHid = policy.Usb.AllowAnyKeyboardOrMouse
                && bundle.HasKeyboardOrMouse
                && (!policy.Usb.DenyCompositeDevicesWithForbiddenFunctions || !bundle.HasForbiddenFunction);
            var userDecision = permissionEvaluator.Evaluate(
                policy,
                ActionKeys.UsbDeviceConnect,
                activeContext,
                identity,
                DateTimeOffset.UtcNow);

            bundle.IsAllowed = safeHid || explicitlyApproved || bundle.IsTrustedBaseline || userDecision.IsAllowed;
        }
        LastSnapshot = bundles;

        var currentIds = bundles.Select(bundle => bundle.RootInstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _known.RemoveWhere(id => !currentIds.Contains(id));

        foreach (var bundle in bundles)
        {
            var isNew = _known.Add(bundle.RootInstanceId);
            if (!isNew && !initial) continue;

            var context = interactiveUserContextProvider.GetActiveConsoleUser();
            var decision = permissionEvaluator.Evaluate(
                policy,
                ActionKeys.UsbDeviceConnect,
                context,
                identity,
                DateTimeOffset.UtcNow);
            var mode = runtimeOverrides.GetUsbMode(policy.Usb.EnforcementMode);
            var details = JsonSerializer.Serialize(new
            {
                bundle.DisplayName,
                bundle.Manufacturer,
                bundle.VendorId,
                bundle.ProductId,
                serialNumber = MaskSerial(bundle.SerialNumber),
                bundle.HardwareId,
                bundle.IsCompositeDevice,
                bundle.HasKeyboardOrMouse,
                bundle.HasForbiddenFunction,
                bundle.IsTrustedBaseline,
                classes = bundle.Classes,
                deviceIds = bundle.DeviceIds
            }, JsonDefaults.Options);

            if (bundle.IsAllowed)
            {
                await auditLogger.WriteAsync(new AuditEvent
                {
                    ActionKey = ActionKeys.UsbDeviceConnect,
                    EventType = "UsbDeviceAllowed",
                    Action = initial ? "device-present-at-startup" : "device-arrival",
                    Method = ResolveAllowMethod(policy.Usb, bundle, decision),
                    Result = "allowed",
                    ReasonCode = ResolveAllowReason(policy.Usb, bundle, decision),
                    PermissionGrantId = decision.IsAllowed ? decision.PermissionGrantId : null,
                    DeviceInstanceId = bundle.RootInstanceId,
                    ResourceName = bundle.DisplayName,
                    Details = details
                }, context, cancellationToken);
                continue;
            }

            await auditLogger.WriteAsync(new AuditEvent
            {
                ActionKey = ActionKeys.UsbDeviceConnect,
                EventType = "UsbDeviceBlocked",
                Action = initial ? "device-present-at-startup" : "device-arrival",
                Method = mode,
                Result = mode.Equals("Block", StringComparison.OrdinalIgnoreCase) ? "block-requested" : "audit-only",
                ReasonCode = decision.ReasonCode,
                PermissionGrantId = decision.PermissionGrantId,
                DeviceInstanceId = bundle.RootInstanceId,
                ResourceName = bundle.DisplayName,
                Details = details
            }, context, cancellationToken);

            if (!mode.Equals("Block", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Unauthorized USB device detected in AuditOnly mode: {Device}", bundle.DisplayName);
                notificationStore.Add(
                    "usb",
                    "Unauthorized USB device detected",
                    $"{bundle.DisplayName} is not an approved keyboard or mouse. AuditOnly mode recorded the device without disabling it.",
                    "Warning",
                    "detected");
                continue;
            }

            var disabled = await controller.DisableAsync(bundle.RootInstanceId, cancellationToken);
            await auditLogger.WriteAsync(new AuditEvent
            {
                ActionKey = ActionKeys.UsbDeviceConnect,
                EventType = disabled ? "UsbDeviceBlocked" : "UsbDeviceBlockFailed",
                Action = "disable-device",
                Method = "PnPUtil",
                Result = disabled ? "blocked" : "failed",
                ReasonCode = disabled ? "UsbDeviceNotApproved" : "DeviceDisableFailed",
                DeviceInstanceId = bundle.RootInstanceId,
                ResourceName = bundle.DisplayName,
                Details = details
            }, context, cancellationToken);

            if (!disabled)
            {
                notificationStore.Add(
                    "usb",
                    "USB device could not be blocked",
                    $"Company DLP detected {bundle.DisplayName}, but Windows did not allow the device to be disabled. Contact IT.",
                    "Error",
                    "block-failed");
                _known.Remove(bundle.RootInstanceId);
            }
            else
            {
                notificationStore.Add(
                    "usb",
                    "USB device blocked",
                    $"{bundle.DisplayName} was blocked because it is not approved by company policy.",
                    "Error",
                    "blocked");

                if (policy.Usb.LockWorkstationOnBlockedDevice) LockWorkStation();
            }
        }
    }

    public void ResetBaseline() => baselineStore.Reset(inventory.GetPresentBundles());

    private static bool IsExplicitlyApproved(UsbPolicy policy, UsbDeviceBundleInfo bundle)
    {
        return policy.ApprovedHardwareIds.Any(value => Matches(value, bundle.HardwareId) || Matches(value, bundle.RootInstanceId))
            || policy.ApprovedVidPid.Any(value => Matches(value, $"{bundle.VendorId}:{bundle.ProductId}") || Matches(value, $"VID_{bundle.VendorId}&PID_{bundle.ProductId}"))
            || policy.ApprovedSerialNumbers.Any(value => Matches(value, bundle.SerialNumber));
    }

    private static string ResolveAllowMethod(UsbPolicy policy, UsbDeviceBundleInfo bundle, PermissionDecision decision)
    {
        if (decision.IsAllowed) return decision.PermissionSource;
        if (IsExplicitlyApproved(policy, bundle)) return "DeviceAllowlist";
        if (bundle.IsTrustedBaseline) return "TrustedBaseline";
        return "SafeHid";
    }

    private static string ResolveAllowReason(UsbPolicy policy, UsbDeviceBundleInfo bundle, PermissionDecision decision)
    {
        if (decision.IsAllowed) return decision.ReasonCode;
        if (IsExplicitlyApproved(policy, bundle)) return "ApprovedUsbDevice";
        if (bundle.IsTrustedBaseline) return "TrustedUsbBaseline";
        return "MouseOrKeyboardAllowed";
    }

    private static bool Matches(string expected, string actual) =>
        !string.IsNullOrWhiteSpace(expected)
        && expected.Trim().Equals(actual?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string MaskSerial(string serial)
    {
        if (string.IsNullOrWhiteSpace(serial)) return "";
        return serial.Length <= 4 ? "****" : new string('*', Math.Min(12, serial.Length - 4)) + serial[^4..];
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();
}
