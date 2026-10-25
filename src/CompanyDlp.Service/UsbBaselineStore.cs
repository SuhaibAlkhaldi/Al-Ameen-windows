using System.Text.Json;
using CompanyDlp.Contracts;

namespace CompanyDlp.Service;

// Persisted baseline entry. VendorId/ProductId give a durable identity that survives a RootInstanceId
// drifting after a driver reinstall or Windows Update reassigns the device's PnP enumeration path -
// RootInstanceId alone (the original key) does not survive that. Both legs are kept (not one
// replacing the other) so either can independently re-establish trust; see MatchesEntry/IsTrusted.
public sealed record UsbBaselineEntry(string RootInstanceId, string VendorId, string ProductId);

public sealed class UsbBaselineStore(PolicyStore policyStore, TimeProvider timeProvider, ILogger<UsbBaselineStore> logger)
{
    // Settle window for a brand-new baseline capture (file does not exist yet on disk). Windows
    // enumerates PnP devices in stages at boot; integrated Bluetooth/webcam chipsets have been
    // observed still finishing WMI/CM registration 10-30s after a service's own startup. 60s gives
    // roughly 2x margin over that worst case while keeping the one-time "nothing is eligible for
    // auto-disable yet" window to a single minute - long enough to reliably outlast slow boot
    // drivers, short enough that it isn't a meaningful standing exposure window. This only ever
    // happens once per machine: once usb-baseline.json exists, every future service start loads it
    // immediately with no settling.
    internal static readonly TimeSpan SettleWindow = TimeSpan.FromSeconds(60);

    private readonly object _sync = new();
    private readonly Dictionary<string, UsbBaselineEntry> _accumulating = new(StringComparer.OrdinalIgnoreCase);
    private List<UsbBaselineEntry>? _trusted;
    private DateTimeOffset? _firstObservedAtUtc;
    private bool _creatingFresh;

    // True only while a brand-new baseline is actively being captured (no usb-baseline.json existed
    // when GetOrCreate was first called) and the settle window has not yet elapsed. UsbProtectionMonitor
    // treats every device as still baseline-pending while this is true (ShouldWithholdDisable).
    public bool IsSettling
    {
        get { lock (_sync) { return _creatingFresh && _trusted is null; } }
    }

    public IReadOnlyList<UsbBaselineEntry> GetOrCreate(IEnumerable<UsbDeviceBundleInfo> current)
    {
        lock (_sync)
        {
            if (_trusted is not null) return _trusted;

            var path = GetPath();

            if (!_creatingFresh)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        _trusted = ParseBaselineJson(File.ReadAllText(path));
                        return _trusted;
                    }
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Unable to load USB baseline; starting a fresh capture.");
                }

                _creatingFresh = true;
                _firstObservedAtUtc = timeProvider.GetUtcNow();
            }

            if (!policyStore.Get().Usb.TrustDevicesPresentAtFirstRun)
            {
                _trusted = [];
                Save(path, _trusted);
                _creatingFresh = false;
                return _trusted;
            }

            foreach (var bundle in current)
            {
                _accumulating[bundle.RootInstanceId] = new UsbBaselineEntry(bundle.RootInstanceId, bundle.VendorId, bundle.ProductId);
            }

            if (!IsSettleWindowElapsed(_firstObservedAtUtc!.Value, timeProvider.GetUtcNow(), SettleWindow))
            {
                // Not yet frozen - callers must also consult IsSettling before treating this as final.
                return _accumulating.Values.ToList();
            }

            _trusted = _accumulating.Values.ToList();
            Save(path, _trusted);
            _creatingFresh = false;
            logger.LogWarning(
                "Created USB baseline with {Count} device roots after a {Seconds:0}s settle window. Disconnect unapproved external devices before first production start.",
                _trusted.Count, SettleWindow.TotalSeconds);
            return _trusted;
        }
    }

    // Explicit, IT-triggered re-baseline ("this machine is clean right now") - unlike GetOrCreate's
    // first-ever capture, this intentionally keeps its immediate-freeze behavior with no settle
    // window, since the administrator is vouching for the machine's state at the moment they run it.
    public void Reset(IEnumerable<UsbDeviceBundleInfo> current)
    {
        lock (_sync)
        {
            _trusted = current.Select(item => new UsbBaselineEntry(item.RootInstanceId, item.VendorId, item.ProductId)).ToList();
            Save(GetPath(), _trusted);
            _creatingFresh = false;
            _accumulating.Clear();
            _firstObservedAtUtc = null;
        }
    }

    private string GetPath()
    {
        var mode = policyStore.Get().Runtime.Mode;
        var root = mode.Equals("Production", StringComparison.OrdinalIgnoreCase)
            ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "CompanyDlp", "usb-baseline.json");
    }

    private static void Save(string path, IEnumerable<UsbBaselineEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var ordered = entries.OrderBy(entry => entry.RootInstanceId, StringComparer.OrdinalIgnoreCase).ToList();
        File.WriteAllText(path, JsonSerializer.Serialize(ordered, JsonDefaults.Options));
    }

    // --- Pure decision logic below: directly unit-testable, no file IO / no real clock, following
    // the same `internal static` pattern UsbProtectionMonitorDecisionTests already exercises. ---

    internal static bool IsSettleWindowElapsed(DateTimeOffset firstObservedAtUtc, DateTimeOffset nowUtc, TimeSpan settleWindow) =>
        nowUtc - firstObservedAtUtc >= settleWindow;

    internal static bool IsTrusted(IReadOnlyCollection<UsbBaselineEntry> entries, UsbDeviceBundleInfo bundle) =>
        entries.Any(entry => MatchesEntry(entry, bundle));

    internal static bool MatchesEntry(UsbBaselineEntry entry, UsbDeviceBundleInfo bundle) =>
        (!string.IsNullOrWhiteSpace(bundle.RootInstanceId)
            && entry.RootInstanceId.Equals(bundle.RootInstanceId, StringComparison.OrdinalIgnoreCase))
        || (!string.IsNullOrWhiteSpace(entry.VendorId) && !string.IsNullOrWhiteSpace(entry.ProductId)
            && entry.VendorId.Equals(bundle.VendorId, StringComparison.OrdinalIgnoreCase)
            && entry.ProductId.Equals(bundle.ProductId, StringComparison.OrdinalIgnoreCase));

    // Backward-compatible read: an already-deployed usb-baseline.json is a bare JSON array of
    // RootInstanceId strings (the original List<string> format). A brand-new file is an array of
    // {RootInstanceId, VendorId, ProductId} objects. Detected by inspecting the first array element's
    // JSON kind - a string means legacy. Legacy entries keep empty VendorId/ProductId, so they can
    // only re-match via the RootInstanceId leg of MatchesEntry - the documented degrade-gracefully
    // behavior for files already deployed in the field. The on-disk file is intentionally left
    // untouched on a mere read; it is only ever rewritten by Reset() or a brand-new fresh capture, so
    // a read never silently mutates a file outside those two explicit write paths.
    internal static List<UsbBaselineEntry> ParseBaselineJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
        {
            return [];
        }

        if (document.RootElement[0].ValueKind == JsonValueKind.String)
        {
            return document.RootElement.EnumerateArray()
                .Select(element => new UsbBaselineEntry(element.GetString() ?? "", "", ""))
                .Where(entry => !string.IsNullOrWhiteSpace(entry.RootInstanceId))
                .ToList();
        }

        return JsonSerializer.Deserialize<List<UsbBaselineEntry>>(json, JsonDefaults.Options) ?? [];
    }
}
