using CompanyDlp.Contracts;

namespace CompanyDlp.Core;

public static class BrowserAuditNotificationPolicy
{
    private const string SilentBackgroundMarker = "silent-background";

    // Network-transport detections are enforcement/audit signals, not reliable proof of a
    // fresh user upload gesture. Picker/drop/paste/showPicker create the user-facing alert.
    // Keeping this fail-safe in the Windows service prevents browser-specific activation
    // heuristics from producing false alerts even if a marker is missing.
    private static readonly HashSet<string> SilentTransportActions = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "formdata-file",
        "formdata-files",
        "xhr-file-upload",
        "fetch-file-upload",
        "beacon-file-upload"
    };

    public static bool ShouldNotify(AuditEvent audit)
    {
        ArgumentNullException.ThrowIfNull(audit);

        if (!audit.EventType.Equals("browser", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!audit.Result.Equals("blocked", StringComparison.OrdinalIgnoreCase))
            return false;

        if (SilentTransportActions.Contains(audit.Action ?? string.Empty))
            return false;

        return !IsSilentBackground(audit.Details);
    }

    public static bool IsSilentBackground(string? details)
    {
        if (string.IsNullOrWhiteSpace(details))
            return false;

        return details.TrimStart().StartsWith(
            SilentBackgroundMarker,
            StringComparison.OrdinalIgnoreCase);
    }
}
