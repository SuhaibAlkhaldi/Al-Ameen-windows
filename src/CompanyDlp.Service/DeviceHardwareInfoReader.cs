using System.Management;
using System.Net.NetworkInformation;

namespace CompanyDlp.Service;

// Reports the two hardware identifiers HeartbeatWorker sends alongside OsVersion/
// OperatingSystemEdition - see AgentHeartbeatRequest.SerialNumber/MacAddress. Both are cached in a
// static field after the first successful read (neither the BIOS serial number nor the primary
// NIC's MAC address ever change while the service is running), so a slow/failed read only ever
// costs one heartbeat tick, not every tick - and the BIOS read in particular goes through WMI, which
// is exactly the kind of call that must never be repeated more often than necessary (see
// InteractiveUserContextProvider's field-evidence comment on what an unbounded/frequent WMI call did
// to this same process on 2026-08-16).
public static class DeviceHardwareInfoReader
{
    private static readonly TimeSpan WmiQueryTimeout = TimeSpan.FromSeconds(5);

    private static string? _cachedSerialNumber;
    private static string? _cachedMacAddress;

    public static string GetSerialNumber()
    {
        if (_cachedSerialNumber != null)
            return _cachedSerialNumber;

        var value = TryReadBiosSerialNumber();
        if (!string.IsNullOrWhiteSpace(value))
            _cachedSerialNumber = value;

        return value;
    }

    public static string GetPrimaryMacAddress()
    {
        if (_cachedMacAddress != null)
            return _cachedMacAddress;

        var value = TryReadPrimaryMacAddress();
        if (!string.IsNullOrWhiteSpace(value))
            _cachedMacAddress = value;

        return value;
    }

    // Same WMI-hang defense as InteractiveUserContextProvider.TryGetInteractiveUsername and
    // SoftwareProtectionMonitor.TryGetMetadata: ReturnImmediately + Options.Timeout bounds the WMI
    // enumeration itself, and the outer Task.Run + Wait(timeout) is a hard backstop that guarantees
    // this method returns within WmiQueryTimeout no matter what WMI does internally. A failed/timed-
    // out read is an empty string for this cycle, never an exception that could interrupt the
    // heartbeat loop.
    private static string TryReadBiosSerialNumber()
    {
        if (!OperatingSystem.IsWindows()) return "";
        try
        {
            var task = Task.Run(() =>
            {
                using var searcher = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BIOS");
                searcher.Options.ReturnImmediately = true;
                searcher.Options.Timeout = WmiQueryTimeout;
                foreach (ManagementObject item in searcher.Get())
                    return item["SerialNumber"]?.ToString()?.Trim() ?? "";
                return "";
            });

            if (task.Wait(WmiQueryTimeout))
                return task.Result;
        }
        catch
        {
            // Ignored - falls through to the "" return below, same as every other WMI reader here.
        }
        return "";
    }

    // NetworkInterface/GetAllNetworkInterfaces is a local system-info API, not RPC-based like WMI, so
    // it doesn't need the same timeout/Task.Run backstop as the BIOS read above.
    private static string TryReadPrimaryMacAddress()
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(IsCandidatePrimaryAdapter);

            if (nic == null) return "";

            var bytes = nic.GetPhysicalAddress().GetAddressBytes();
            return bytes.Length == 0 ? "" : string.Join(":", bytes.Select(b => b.ToString("X2")));
        }
        catch
        {
            return "";
        }
    }

    // "Primary" = the first physical, currently-up Ethernet/Wi-Fi adapter, in the order Windows
    // reports them - there's no reliable, universally-available "this is THE primary NIC" signal to
    // rank candidates by, so this takes the first match rather than guessing further (e.g. by link
    // speed or interface index). NetworkInterface has no IsVirtual property, so virtual/VPN/loopback/
    // TAP adapters (Hyper-V vEthernet, VMware, WireGuard/OpenVPN TAP, etc. - which do report as
    // NetworkInterfaceType.Ethernet) are filtered out by name/description instead.
    private static bool IsCandidatePrimaryAdapter(NetworkInterface nic)
    {
        if (nic.OperationalStatus != OperationalStatus.Up) return false;
        if (nic.NetworkInterfaceType != NetworkInterfaceType.Ethernet &&
            nic.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) return false;
        if (nic.GetPhysicalAddress().GetAddressBytes().Length == 0) return false;

        return !ContainsVirtualAdapterMarker(nic.Name) && !ContainsVirtualAdapterMarker(nic.Description);
    }

    private static readonly string[] VirtualAdapterMarkers = ["Virtual", "VPN", "Loopback", "TAP"];

    private static bool ContainsVirtualAdapterMarker(string value) =>
        !string.IsNullOrEmpty(value) &&
        VirtualAdapterMarkers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
