using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using CompanyDlp.Contracts;

namespace CompanyDlp.Service;

public sealed class UsbDeviceInventory(ILogger<UsbDeviceInventory> logger)
{
    private static readonly HashSet<string> ForbiddenClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "DiskDrive", "WPD", "Ports", "Net", "Image", "Camera", "Printer", "PrintQueue",
        "Bluetooth", "Modem", "CDROM", "Media", "AudioEndpoint", "SmartCardReader", "Sensor"
    };

    public IReadOnlyList<UsbDeviceBundleInfo> GetPresentBundles()
    {
        if (!OperatingSystem.IsWindows()) return [];

        try
        {
            var nodes = EnumerateNodes();
            var bundles = new Dictionary<string, List<PnpNode>>(StringComparer.OrdinalIgnoreCase);

            foreach (var node in nodes)
            {
                var root = FindPhysicalUsbRoot(node.DeviceId);
                if (string.IsNullOrWhiteSpace(root)) continue;
                if (!bundles.TryGetValue(root, out var list))
                {
                    list = [];
                    bundles[root] = list;
                }
                list.Add(node);
            }

            return bundles.Select(pair => BuildBundle(pair.Key, pair.Value)).OrderBy(bundle => bundle.DisplayName).ToList();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unable to enumerate USB device bundles.");
            return [];
        }
    }

    private static List<PnpNode> EnumerateNodes()
    {
        var result = new List<PnpNode>();
        using var searcher = new ManagementObjectSearcher("SELECT DeviceID, Name, PNPClass, Manufacturer, Status FROM Win32_PnPEntity WHERE DeviceID IS NOT NULL");
        foreach (ManagementObject item in searcher.Get())
        {
            var id = Convert.ToString(item["DeviceID"]) ?? string.Empty;
            var deviceClass = Convert.ToString(item["PNPClass"]) ?? "Unknown";
            var relevant = id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)
                || id.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase)
                || id.StartsWith("USBSTOR\\", StringComparison.OrdinalIgnoreCase)
                || ForbiddenClasses.Contains(deviceClass)
                || deviceClass.Equals("Keyboard", StringComparison.OrdinalIgnoreCase)
                || deviceClass.Equals("Mouse", StringComparison.OrdinalIgnoreCase);
            if (!relevant) continue;

            result.Add(new PnpNode(
                id,
                Convert.ToString(item["Name"]) ?? "Unknown device",
                deviceClass,
                Convert.ToString(item["Manufacturer"]) ?? "",
                Convert.ToString(item["Status"]) ?? ""));
        }
        return result;
    }

    private static UsbDeviceBundleInfo BuildBundle(string rootId, List<PnpNode> nodes)
    {
        var classes = nodes.Select(node => node.DeviceClass).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var hasInput = nodes.Any(node =>
            node.DeviceClass.Equals("Keyboard", StringComparison.OrdinalIgnoreCase) ||
            node.DeviceClass.Equals("Mouse", StringComparison.OrdinalIgnoreCase) ||
            node.Name.Contains("keyboard", StringComparison.OrdinalIgnoreCase) ||
            node.Name.Contains("mouse", StringComparison.OrdinalIgnoreCase) ||
            node.Name.Contains("touchpad", StringComparison.OrdinalIgnoreCase));

        var hasForbidden = nodes.Any(node => ForbiddenClasses.Contains(node.DeviceClass));
        var display = nodes.FirstOrDefault(node =>
            node.DeviceClass.Equals("Keyboard", StringComparison.OrdinalIgnoreCase) ||
            node.DeviceClass.Equals("Mouse", StringComparison.OrdinalIgnoreCase))?.Name
            ?? nodes.FirstOrDefault(node => !node.Name.Contains("USB Composite", StringComparison.OrdinalIgnoreCase))?.Name
            ?? "USB device";

        var (vendorId, productId, serialNumber) = ParsePhysicalIdentity(rootId);
        var manufacturer = nodes.Select(node => node.Manufacturer)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";

        return new UsbDeviceBundleInfo
        {
            RootInstanceId = rootId,
            DisplayName = display,
            Classes = classes,
            DeviceIds = nodes.Select(node => node.DeviceId).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Manufacturer = manufacturer,
            VendorId = vendorId,
            ProductId = productId,
            SerialNumber = serialNumber,
            HardwareId = string.IsNullOrWhiteSpace(vendorId) || string.IsNullOrWhiteSpace(productId)
                ? rootId
                : $"USB\\VID_{vendorId}&PID_{productId}",
            IsCompositeDevice = nodes.Any(node => node.Name.Contains("USB Composite", StringComparison.OrdinalIgnoreCase)),
            HasKeyboardOrMouse = hasInput,
            HasForbiddenFunction = hasForbidden,
            IsAllowed = hasInput && !hasForbidden
        };
    }


    private static (string VendorId, string ProductId, string SerialNumber) ParsePhysicalIdentity(string rootId)
    {
        var vendorId = "";
        var productId = "";
        var serialNumber = "";
        var parts = rootId.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            foreach (var token in parts[1].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.StartsWith("VID_", StringComparison.OrdinalIgnoreCase)) vendorId = token[4..];
                if (token.StartsWith("PID_", StringComparison.OrdinalIgnoreCase)) productId = token[4..];
            }
        }
        if (parts.Length >= 3) serialNumber = parts[^1];
        return (vendorId, productId, serialNumber);
    }

    private static string? FindPhysicalUsbRoot(string deviceId)
    {
        if (CM_Locate_DevNodeW(out var devInst, deviceId, 0) != 0) return null;

        string? candidate = null;
        var current = devInst;
        for (var depth = 0; depth < 16; depth++)
        {
            var currentId = GetDeviceId(current);
            if (currentId.StartsWith("USB\\VID_", StringComparison.OrdinalIgnoreCase))
            {
                candidate = currentId;
            }

            if (CM_Get_Parent(out var parent, current, 0) != 0) break;
            current = parent;
        }

        return candidate;
    }

    private static string GetDeviceId(uint devInst)
    {
        var buffer = new StringBuilder(1024);
        return CM_Get_Device_IDW(devInst, buffer, buffer.Capacity, 0) == 0 ? buffer.ToString() : string.Empty;
    }

    private sealed record PnpNode(string DeviceId, string Name, string DeviceClass, string Manufacturer, string Status);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out uint parentDevInst, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_IDW(uint devInst, StringBuilder buffer, int bufferLength, uint flags);
}
