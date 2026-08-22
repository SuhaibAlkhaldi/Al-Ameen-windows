using System.Text.RegularExpressions;

namespace CompanyDlp.Contracts;

public enum ExtensionPlatform { Chrome, Edge, Firefox }

public enum ExtensionForceInstallStatus
{
    // Both id and update URL are blank - force-install is simply not configured for this browser.
    // A legitimate, silent no-op: nothing is forced, and (see BrowserPolicyManager) nothing gets
    // blocked either, since there would be no working alternative to fall back to.
    NotConfigured,

    // Well-formed id + HTTPS update URL, neither an unfilled template placeholder.
    Valid,

    // Present but malformed, or an unfilled "REPLACE_..." placeholder, or only one of the pair is
    // set. Distinguishing this from NotConfigured is the whole point of this type: a caller must
    // treat "someone tried to configure this and got it wrong" very differently from "nobody
    // configured it" - see the 2026-08-22 incident this exists to prevent, where a real deployment
    // shipped literal "REPLACE_AFTER_PUBLISHING_EXTENSION" placeholders with BlockUnapprovedExtensions
    // enabled: every extension (including Ameen's own) got blocked because the force-install entry
    // Chrome/Edge actually validated and accepted was garbage, while the independent
    // ExtensionInstallBlocklist="*" write went through unconditionally regardless.
    Invalid
}

/// <summary>
/// Single source of truth for "is this extensionId/updateUrl pair something we should actually trust
/// enough to force-install and, separately, to justify blocking every other extension" - shared by the
/// agent (BrowserPolicyManager, which writes the registry policy) and the backend
/// (TenantPolicySanitizer, which is the first and cheapest place to reject a bad value before it ever
/// reaches a device). Keeping this logic in CompanyDlp.Contracts rather than duplicated in both means a
/// future format change (e.g. a new browser) only needs updating here.
/// </summary>
public static class ExtensionPolicyValidator
{
    // Chrome/Edge extension IDs are exactly 32 characters, each one a base16 nibble of a SHA-256
    // hash remapped from 0-9a-f to a-p (see scripts/pack-browser-extension.ps1's own derivation,
    // which computes an ID this same way from the packing key).
    private static readonly Regex ChromiumIdPattern = new("^[a-p]{32}$", RegexOptions.Compiled);

    // Firefox's documented "Extension ID format" (see MDN's WebExtensions/manifest.json/browser_
    // specific_settings docs): either an email-like string or a GUID wrapped in braces. Matches
    // firefox-extension/manifest.json's browser_specific_settings.gecko.id
    // ("company-dlp@company.local").
    private static readonly Regex FirefoxIdPattern = new(
        @"^(\{[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}|[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+)$",
        RegexOptions.Compiled);

    public static bool LooksLikePlaceholder(string? value) =>
        value is null || string.IsNullOrWhiteSpace(value) || value.TrimStart().StartsWith("REPLACE", StringComparison.OrdinalIgnoreCase);

    public static bool IsValidExtensionId(string? value, ExtensionPlatform platform)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return platform == ExtensionPlatform.Firefox
            ? FirefoxIdPattern.IsMatch(value)
            : ChromiumIdPattern.IsMatch(value);
    }

    public static bool IsValidUpdateUrl(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps;

    public static ExtensionForceInstallStatus Validate(string? extensionId, string? updateUrl, ExtensionPlatform platform)
    {
        if (string.IsNullOrWhiteSpace(extensionId) && string.IsNullOrWhiteSpace(updateUrl))
            return ExtensionForceInstallStatus.NotConfigured;

        if (LooksLikePlaceholder(extensionId) || LooksLikePlaceholder(updateUrl))
            return ExtensionForceInstallStatus.Invalid;

        if (!IsValidExtensionId(extensionId, platform) || !IsValidUpdateUrl(updateUrl))
            return ExtensionForceInstallStatus.Invalid;

        return ExtensionForceInstallStatus.Valid;
    }
}
