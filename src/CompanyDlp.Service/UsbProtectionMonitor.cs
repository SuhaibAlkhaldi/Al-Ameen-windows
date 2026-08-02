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

    // Windows enumerates a newly-arrived USB device's PnP class information in stages, so the very first
    // tick that observes a device can see an incomplete Classes list (e.g. mass-storage devices briefly
    // report only "USB" before "DiskDrive" appears). Acting on that first tick — writing the one-time
    // arrival audit/notification, or (in Block enforcement mode) disabling the device — could therefore
    // permanently mislabel the ActionKey, or worse, evaluate the disable decision itself against the wrong
    // (possibly more permissive) key. Every first-arrival device is held here and only acted on — audited
    // and, if still denied, disabled — once the resolved gating key agrees across two consecutive ticks.
    // This only delays the brief window right after first arrival: a device already known/classified from
    // a prior tick is never re-routed through this dictionary, so normal operation has no added delay.
    private readonly Dictionary<string, PendingUsbArrival> _pendingArrivals = new(StringComparer.OrdinalIgnoreCase);

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

        // Every bundle's grant decision is computed exactly once here and reused below for the audit
        // record and notification, instead of re-evaluating independently later in the same tick — two
        // separate live evaluations of the same grant set could, in principle, diverge (e.g. from a grant
        // change landing between the two calls) and made the gating outcome and the reported reason/grant
        // id two separate sources of truth instead of one.
        var decisionsByDevice = new Dictionary<string, PermissionDecision>(StringComparer.OrdinalIgnoreCase);

        foreach (var bundle in bundles)
        {
            bundle.IsTrustedBaseline = baseline.Contains(bundle.RootInstanceId);
            var explicitlyApproved = IsExplicitlyApproved(policy.Usb, bundle);
            var safeHid = IsSafeHid(policy.Usb, bundle);
            var userDecision = permissionEvaluator.Evaluate(
                policy,
                ResolveGatingActionKey(bundle),
                activeContext,
                identity,
                DateTimeOffset.UtcNow);
            decisionsByDevice[bundle.RootInstanceId] = userDecision;

            bundle.IsAllowed = ResolveIsAllowed(safeHid, explicitlyApproved, bundle.IsTrustedBaseline, userDecision.IsAllowed);
        }
        LastSnapshot = bundles;

        var currentIds = bundles.Select(bundle => bundle.RootInstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _known.RemoveWhere(id => !currentIds.Contains(id));
        foreach (var pendingId in _pendingArrivals.Keys.Where(id => !currentIds.Contains(id)).ToList())
        {
            _pendingArrivals.Remove(pendingId);
        }

        foreach (var bundle in bundles)
        {
            var isNew = _known.Add(bundle.RootInstanceId);
            var isSettling = _pendingArrivals.ContainsKey(bundle.RootInstanceId);
            if (!isNew && !initial && !isSettling) continue;

            var gatingActionKey = ResolveGatingActionKey(bundle);
            var mode = runtimeOverrides.GetUsbMode(policy.Usb.EnforcementMode);
            var decision = decisionsByDevice[bundle.RootInstanceId];
            _pendingArrivals.TryGetValue(bundle.RootInstanceId, out var pending);
            var wasInitial = pending?.Initial ?? initial;

            // Every first-arrival device — allowed, AuditOnly-blocked, or Block-mode-blocked alike — waits
            // for the gating key to agree across two consecutive ticks before anything is acted on: no
            // audit/notification write, and (this is the part that used to be immediate) no disable call
            // either. A device still mid-settle is neither treated as allowed nor as finally blocked; it
            // simply has no decision recorded yet, and is re-evaluated fresh next tick.
            if (isNew || isSettling)
            {
                if (pending is not null && pending.GatingActionKey == gatingActionKey)
                {
                    _pendingArrivals.Remove(bundle.RootInstanceId);
                    // Two consecutive ticks agree — falls through to act on the now-settled record.
                }
                else
                {
                    _pendingArrivals[bundle.RootInstanceId] = new PendingUsbArrival(gatingActionKey, wasInitial);
                    continue;
                }
            }

            var deviceTypeLabel = DescribeGatedDeviceType(gatingActionKey);
            var context = activeContext;
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
                    ActionKey = gatingActionKey,
                    EventType = "UsbDeviceAllowed",
                    Action = wasInitial ? "device-present-at-startup" : "device-arrival",
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
                ActionKey = gatingActionKey,
                EventType = "UsbDeviceBlocked",
                Action = wasInitial ? "device-present-at-startup" : "device-arrival",
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
                logger.LogWarning("Unauthorized {DeviceType} detected in AuditOnly mode: {Device}", deviceTypeLabel, bundle.DisplayName);
                notificationStore.Add(
                    "usb",
                    $"Unauthorized {deviceTypeLabel} detected",
                    $"{bundle.DisplayName} is not an approved {deviceTypeLabel.ToLowerInvariant()}. AuditOnly mode recorded the device without disabling it.",
                    "Warning",
                    "detected");
                continue;
            }

            var disabled = await controller.DisableAsync(bundle.RootInstanceId, cancellationToken);
            await auditLogger.WriteAsync(new AuditEvent
            {
                ActionKey = gatingActionKey,
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
                    $"{deviceTypeLabel} could not be blocked",
                    $"Company DLP detected {bundle.DisplayName}, but Windows did not allow the device to be disabled. Contact IT.",
                    "Error",
                    "block-failed");
                _known.Remove(bundle.RootInstanceId);
            }
            else
            {
                notificationStore.Add(
                    "usb",
                    $"{deviceTypeLabel} blocked",
                    $"{bundle.DisplayName} was blocked because it is not approved by company policy.",
                    "Error",
                    "blocked");

                if (policy.Usb.LockWorkstationOnBlockedDevice) LockWorkStation();
            }
        }
    }

    public void ResetBaseline() => baselineStore.Reset(inventory.GetPresentBundles());

    // The following decision-rule methods are `internal` (not `private`) solely so CompanyDlp.Tests
    // (InternalsVisibleTo, see the .csproj) can test the actual device-gating decisions directly -
    // given a device/policy description, what does the monitor decide - without needing real USB
    // hardware, WMI enumeration, or pnputil.exe. TickAsync's own orchestration (the two-tick arrival
    // settle state machine, real UsbDeviceInventory/UsbDeviceController I/O, audit/notification
    // dispatch) is not covered by those tests - see the test file's own header comment for why.

    // Windows PNPClass names (already collected per-bundle by UsbDeviceInventory) that distinguish the
    // two specifically-grantable USB permissions from the general usb.device-connect gate: "DiskDrive" is
    // the standard class for USB mass-storage devices (GUID_DEVCLASS_DISKDRIVE, {4d36e967-...}), "WPD" is
    // Windows Portable Device — MTP/mobile/media devices (GUID_DEVCLASS_WPD, {eec5ad98-...}). Anything else
    // (HID, keyboards/mice, other classes) falls back to the general permission.
    internal static string ResolveGatingActionKey(UsbDeviceBundleInfo bundle)
    {
        if (bundle.Classes.Any(value => value.Equals("DiskDrive", StringComparison.OrdinalIgnoreCase))) return ActionKeys.UsbStorage;
        if (bundle.Classes.Any(value => value.Equals("WPD", StringComparison.OrdinalIgnoreCase))) return ActionKeys.UsbMobileDevice;
        return ActionKeys.UsbDeviceConnect;
    }

    internal static string DescribeGatedDeviceType(string gatingActionKey) => gatingActionKey switch
    {
        ActionKeys.UsbStorage => "USB storage device",
        ActionKeys.UsbMobileDevice => "USB mobile device",
        _ => "USB device"
    };

    internal static bool IsExplicitlyApproved(UsbPolicy policy, UsbDeviceBundleInfo bundle)
    {
        return policy.ApprovedHardwareIds.Any(value => Matches(value, bundle.HardwareId) || Matches(value, bundle.RootInstanceId))
            || policy.ApprovedVidPid.Any(value => Matches(value, $"{bundle.VendorId}:{bundle.ProductId}") || Matches(value, $"VID_{bundle.VendorId}&PID_{bundle.ProductId}"))
            || policy.ApprovedSerialNumbers.Any(value => Matches(value, bundle.SerialNumber));
    }

    internal static bool IsSafeHid(UsbPolicy policy, UsbDeviceBundleInfo bundle) =>
        policy.AllowAnyKeyboardOrMouse
        && bundle.HasKeyboardOrMouse
        && (!policy.DenyCompositeDevicesWithForbiddenFunctions || !bundle.HasForbiddenFunction);

    internal static bool ResolveIsAllowed(bool safeHid, bool explicitlyApproved, bool isTrustedBaseline, bool grantAllowed) =>
        safeHid || explicitlyApproved || isTrustedBaseline || grantAllowed;

    internal static string ResolveAllowMethod(UsbPolicy policy, UsbDeviceBundleInfo bundle, PermissionDecision decision)
    {
        if (decision.IsAllowed) return decision.PermissionSource;
        if (IsExplicitlyApproved(policy, bundle)) return "DeviceAllowlist";
        if (bundle.IsTrustedBaseline) return "TrustedBaseline";
        return "SafeHid";
    }

    internal static string ResolveAllowReason(UsbPolicy policy, UsbDeviceBundleInfo bundle, PermissionDecision decision)
    {
        if (decision.IsAllowed) return decision.ReasonCode;
        if (IsExplicitlyApproved(policy, bundle)) return "ApprovedUsbDevice";
        if (bundle.IsTrustedBaseline) return "TrustedUsbBaseline";
        return "MouseOrKeyboardAllowed";
    }

    internal static bool Matches(string expected, string actual) =>
        !string.IsNullOrWhiteSpace(expected)
        && expected.Trim().Equals(actual?.Trim(), StringComparison.OrdinalIgnoreCase);

    internal static string MaskSerial(string serial)
    {
        if (string.IsNullOrWhiteSpace(serial)) return "";
        return serial.Length <= 4 ? "****" : new string('*', Math.Min(12, serial.Length - 4)) + serial[^4..];
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    private sealed record PendingUsbArrival(string GatingActionKey, bool Initial);
}
