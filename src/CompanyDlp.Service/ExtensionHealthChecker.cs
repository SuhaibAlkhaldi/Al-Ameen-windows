using System.Text.Json;
using Microsoft.Win32;

namespace CompanyDlp.Service;

/// <summary>
/// Best-effort answer to "is the force-installed extension actually present in the browser profile
/// Windows is currently showing the user" - the one question none of the registry-policy-writing code
/// in BrowserPolicyManager can answer on its own, since a successful registry write only proves the
/// *policy* landed, not that Chrome/Edge/Firefox actually acted on it (which is exactly how the
/// "everything blocked, nothing protected" incident this exists to catch went undetected for as long as
/// it did).
///
/// Deliberately narrow in what it claims: this checks for the *presence* of an entry for the target
/// extension ID in the browser's own on-disk profile state, not whether that entry is enabled/active.
/// Chrome's Secure Preferences schema for "is this extension actually running" (disable_reasons,
/// active_bit, location, ...) was inspected empirically against a real installed Chrome profile while
/// building this and turned out more version-dependent than could be relied on here with confidence -
/// asserting a specific "enabled" encoding that turned out to be wrong would recreate the exact failure
/// mode this feature exists to prevent (a health check that confidently reports "fine" when it isn't).
/// Presence-after-a-real-launch is a weaker signal, but one this class can stand behind.
///
/// Scoped to the current active console user only (mirrors ResolveBlockDownloads/
/// ResolveDisableGameCapture's existing pattern in BrowserPolicyManager) rather than enumerating every
/// local profile - the interactive user's browser is what actually matters for whether protection is
/// working right now, and this avoids adding a second, independent profile-enumeration mechanism next to
/// the one uninstall-production.ps1 already has for a different purpose.
/// </summary>
public sealed class ExtensionHealthChecker(ILogger<ExtensionHealthChecker> logger)
{
    public bool IsChromeExtensionPresent(string userSid, string extensionId) =>
        IsChromiumExtensionPresent(userSid, extensionId, @"Google\Chrome\User Data");

    public bool IsEdgeExtensionPresent(string userSid, string extensionId) =>
        IsChromiumExtensionPresent(userSid, extensionId, @"Microsoft\Edge\User Data");

    public bool IsFirefoxExtensionPresent(string userSid, string extensionId)
    {
        var profilePath = TryGetProfileImagePath(userSid);
        if (profilePath is null) return true; // Can't determine - don't report a false alarm.

        var profilesRoot = Path.Combine(profilePath, "AppData", "Roaming", "Mozilla", "Firefox", "Profiles");
        if (!Directory.Exists(profilesRoot)) return true; // Firefox never launched for this user - nothing to check yet.

        try
        {
            foreach (var profileDir in Directory.EnumerateDirectories(profilesRoot))
            {
                var extensionsJsonPath = Path.Combine(profileDir, "extensions.json");
                if (!File.Exists(extensionsJsonPath)) continue;
                if (ContainsExtensionId(extensionsJsonPath, "addons", "id", extensionId)) return true;
            }
        }
        catch (Exception exception)
        {
            // A locked/mid-write file (Firefox actively running) or a permissions quirk should never
            // be reported as "extension missing" - only an actual, clean negative result should.
            logger.LogDebug(exception, "Could not evaluate Firefox extension presence for {ExtensionId}.", extensionId);
            return true;
        }

        return false;
    }

    private bool IsChromiumExtensionPresent(string userSid, string extensionId, string userDataRelativePath)
    {
        var profilePath = TryGetProfileImagePath(userSid);
        if (profilePath is null) return true;

        var userDataRoot = Path.Combine(profilePath, "AppData", "Local", userDataRelativePath);
        if (!Directory.Exists(userDataRoot)) return true; // Browser never launched for this user.

        try
        {
            var browserProfileDirs = Directory.EnumerateDirectories(userDataRoot)
                .Where(dir =>
                {
                    var name = Path.GetFileName(dir);
                    return name.Equals("Default", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase);
                });

            foreach (var profileDir in browserProfileDirs)
            {
                // Secure Preferences (HMAC-integrity-checked by Chrome itself) mirrors Preferences for
                // extension settings and is what a fresh profile actually has; fall back to Preferences
                // for older profile layouts. Reading either here is read-only - no MAC validation needed
                // for that.
                var candidate = Path.Combine(profileDir, "Secure Preferences");
                if (!File.Exists(candidate)) candidate = Path.Combine(profileDir, "Preferences");
                if (!File.Exists(candidate)) continue;

                if (ContainsExtensionSetting(candidate, extensionId)) return true;
            }
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not evaluate Chromium extension presence for {ExtensionId}.", extensionId);
            return true;
        }

        return false;
    }

    private static bool ContainsExtensionSetting(string preferencesFilePath, string extensionId)
    {
        using var stream = File.Open(preferencesFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.TryGetProperty("extensions", out var extensions)
            && extensions.TryGetProperty("settings", out var settings)
            && settings.TryGetProperty(extensionId, out _);
    }

    private static bool ContainsExtensionId(string jsonFilePath, string arrayPropertyName, string idPropertyName, string extensionId)
    {
        using var stream = File.Open(jsonFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty(arrayPropertyName, out var array) || array.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var entry in array.EnumerateArray())
        {
            if (entry.TryGetProperty(idPropertyName, out var idValue)
                && string.Equals(idValue.GetString(), extensionId, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string? TryGetProfileImagePath(string userSid)
    {
        if (string.IsNullOrWhiteSpace(userSid)) return null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{userSid}");
            return key?.GetValue("ProfileImagePath") as string;
        }
        catch
        {
            return null;
        }
    }
}
