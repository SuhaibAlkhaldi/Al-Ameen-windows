using System.Text.RegularExpressions;
using CompanyDlp.Contracts;

namespace CompanyDlp.AdminApi.Services;

public static class TenantPolicySanitizer
{
    private static readonly IReadOnlySet<string> SensitiveTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        SensitiveRuleTypes.Keyword,
        SensitiveRuleTypes.ExactValue,
        SensitiveRuleTypes.Regex,
        SensitiveRuleTypes.AnyEmail
    };

    public static string? Normalize(DlpPolicy policy)
    {
        policy.PolicyVersion = (policy.PolicyVersion ?? "").Trim();
        if (policy.PolicyVersion.Length > 100) return "PolicyVersionTooLong";

        policy.Runtime ??= new RuntimePolicy();
        policy.Clipboard ??= new ClipboardPolicy();
        policy.Browser ??= new BrowserPolicy();
        policy.Usb ??= new UsbPolicy();
        policy.Screen ??= new ScreenPolicy();
        policy.Watermark ??= new WatermarkPolicy();
        policy.Notifications ??= new NotificationPolicy();
        policy.Software ??= new SoftwarePolicy();
        policy.FileProtection ??= new FileProtectionPolicy();
        policy.FileClassification ??= new FileClassificationPolicy();
        policy.Backend ??= new BackendPolicy();
        policy.Permissions ??= new PermissionPolicy();
        policy.SensitiveRules ??= [];

        policy.Runtime.PolicyReapplySeconds = Math.Clamp(policy.Runtime.PolicyReapplySeconds, 5, 3600);
        policy.Runtime.SessionAgentPollSeconds = Math.Clamp(policy.Runtime.SessionAgentPollSeconds, 1, 3600);
        policy.Clipboard.FragmentWindowSeconds = Math.Clamp(policy.Clipboard.FragmentWindowSeconds, 1, 3600);
        policy.Clipboard.MaxFragments = Math.Clamp(policy.Clipboard.MaxFragments, 1, 100);
        policy.Usb.PollSeconds = Math.Clamp(policy.Usb.PollSeconds, 1, 3600);
        policy.Screen.RecorderPollMilliseconds = Math.Clamp(policy.Screen.RecorderPollMilliseconds, 100, 60000);
        policy.Notifications.DurationSeconds = Math.Clamp(policy.Notifications.DurationSeconds, 1, 60);
        policy.Notifications.DuplicateWindowSeconds = Math.Clamp(policy.Notifications.DuplicateWindowSeconds, 0, 60);
        policy.Watermark.Opacity = Math.Clamp(policy.Watermark.Opacity, 0.01, 1.0);
        policy.Watermark.FontSize = Math.Clamp(policy.Watermark.FontSize, 8, 96);
        policy.Watermark.HorizontalSpacing = Math.Clamp(policy.Watermark.HorizontalSpacing, 100, 4000);
        policy.Watermark.VerticalSpacing = Math.Clamp(policy.Watermark.VerticalSpacing, 50, 4000);
        policy.FileProtection.MaximumFileSizeBytes = Math.Clamp(policy.FileProtection.MaximumFileSizeBytes, 1L, 1L << 40);
        policy.FileClassification.TimeoutSeconds = Math.Clamp(policy.FileClassification.TimeoutSeconds, 1, 300);
        policy.FileClassification.MaximumFileSizeBytes = Math.Clamp(policy.FileClassification.MaximumFileSizeBytes, 1L, 1L << 40);
        policy.Backend.RequestTimeoutSeconds = Math.Clamp(policy.Backend.RequestTimeoutSeconds, 1, 120);
        policy.Backend.AuditBatchSize = Math.Clamp(policy.Backend.AuditBatchSize, 1, 500);
        policy.Backend.AuditSyncSeconds = Math.Clamp(policy.Backend.AuditSyncSeconds, 1, 3600);
        policy.Backend.PolicySyncSeconds = Math.Clamp(policy.Backend.PolicySyncSeconds, 5, 3600);
        policy.Backend.HeartbeatSeconds = Math.Clamp(policy.Backend.HeartbeatSeconds, 5, 3600);

        var listError = NormalizeList(policy.Usb.ApprovedHardwareIds, 1000, 1000, "ApprovedHardwareIds");
        if (listError is not null) return listError;
        listError = NormalizeList(policy.Usb.ApprovedVidPid, 1000, 200, "ApprovedVidPid");
        if (listError is not null) return listError;
        listError = NormalizeList(policy.Usb.ApprovedSerialNumbers, 1000, 500, "ApprovedSerialNumbers");
        if (listError is not null) return listError;
        listError = NormalizeList(policy.Screen.BlockedRecorderProcessNames, 1000, 260, "BlockedRecorderProcessNames");
        if (listError is not null) return listError;
        listError = NormalizeList(policy.Screen.BlockedScreenshotToolProcessNames, 1000, 260, "BlockedScreenshotToolProcessNames");
        if (listError is not null) return listError;
        listError = NormalizeList(policy.Software.AllowedPublishers, 1000, 500, "AllowedPublishers");
        if (listError is not null) return listError;
        listError = NormalizeList(policy.Software.AllowedSha256, 10000, 128, "AllowedSha256");
        if (listError is not null) return listError;

        if (policy.SensitiveRules.Count > 1000) return "TooManySensitiveRules";
        var ruleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in policy.SensitiveRules)
        {
            if (rule is null) return "NullSensitiveRule";
            rule.Id = (rule.Id ?? "").Trim();
            rule.Name = (rule.Name ?? "").Trim();
            rule.Type = (rule.Type ?? "").Trim();
            rule.Value ??= "";
            rule.Pattern ??= "";
            if (string.IsNullOrWhiteSpace(rule.Id) || rule.Id.Length > 200 || !ruleIds.Add(rule.Id))
                return "InvalidOrDuplicateSensitiveRuleId";
            if (rule.Name.Length > 300 || !SensitiveTypes.Contains(rule.Type))
                return "InvalidSensitiveRule";
            if (rule.Value.Length > 4000 || rule.Pattern.Length > 4000)
                return "SensitiveRuleValueTooLong";
            rule.MinimumBlockedFragmentLength = Math.Clamp(rule.MinimumBlockedFragmentLength, 2, 1000);
            if (rule.Type.Equals(SensitiveRuleTypes.Regex, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(rule.Pattern)) return "RegexPatternRequired";
                try
                {
                    _ = new Regex(rule.Pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
                }
                catch (ArgumentException)
                {
                    return "InvalidRegexPattern";
                }
            }
        }

        policy.Permissions.DefaultPermissions ??= new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var normalizedDefaults = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var actionKey in ActionKeys.All)
        {
            var configured = policy.Permissions.DefaultPermissions
                .FirstOrDefault(value => value.Key.Equals(actionKey, StringComparison.OrdinalIgnoreCase));
            normalizedDefaults[actionKey] = configured.Key is not null && configured.Value;
        }
        policy.Permissions.DefaultPermissions = normalizedDefaults;
        policy.Permissions.Grants ??= [];

        var extensionError = ValidateExtensionPolicy(policy.Browser);
        if (extensionError is not null) return extensionError;

        return ValidateSimpleStrings(policy);
    }

    // Rejects a bad extensionId/updateUrl pair here, at the one place every tenant's policy passes
    // through before it is ever signed and shipped to a device - the alternative is BrowserPolicyManager
    // silently discovering the same problem machine-by-machine, which is exactly how a real deployment
    // shipped literal "REPLACE_..." placeholders with BlockUnapprovedExtensions enabled (every
    // extension, including Ameen's own, got blocked with no working replacement). NotConfigured (both
    // fields blank) is deliberately allowed through unchanged - a tenant that hasn't set up self-hosted
    // extension distribution yet is a valid, if unprotected, state; a tenant that tried and typo'd/left
    // a placeholder is not.
    private static string? ValidateExtensionPolicy(BrowserPolicy browser)
    {
        if (ExtensionPolicyValidator.Validate(browser.ChromeExtensionId, browser.ChromeExtensionUpdateUrl, ExtensionPlatform.Chrome)
            == ExtensionForceInstallStatus.Invalid)
            return "InvalidChromeExtensionForceInstall";

        if (ExtensionPolicyValidator.Validate(browser.EdgeExtensionId, browser.EdgeExtensionUpdateUrl, ExtensionPlatform.Edge)
            == ExtensionForceInstallStatus.Invalid)
            return "InvalidEdgeExtensionForceInstall";

        if (ExtensionPolicyValidator.Validate(browser.FirefoxExtensionId, browser.FirefoxExtensionUpdateUrl, ExtensionPlatform.Firefox)
            == ExtensionForceInstallStatus.Invalid)
            return "InvalidFirefoxExtensionForceInstall";

        return null;
    }

    private static string? ValidateSimpleStrings(DlpPolicy policy)
    {
        if ((policy.Runtime.AuditDirectory ?? "").Length > 1000
            || (policy.Browser.ChromeExtensionId ?? "").Length > 200
            || (policy.Browser.EdgeExtensionId ?? "").Length > 200
            || (policy.Browser.FirefoxExtensionId ?? "").Length > 200
            || (policy.Browser.ChromeExtensionUpdateUrl ?? "").Length > 2000
            || (policy.Browser.EdgeExtensionUpdateUrl ?? "").Length > 2000
            || (policy.Browser.FirefoxExtensionUpdateUrl ?? "").Length > 2000
            || (policy.Watermark.Prefix ?? "").Length > 500
            || (policy.FileProtection.KeyProvider ?? "").Length > 100
            || (policy.FileClassification.Provider ?? "").Length > 100
            || (policy.FileClassification.BackendPath ?? "").Length > 1000
            || (policy.Backend.BaseUrl ?? "").Length > 2000
            || (policy.Backend.Mode ?? "").Length > 100
            || (policy.Backend.AuthenticationMode ?? "").Length > 100
            || (policy.Backend.CredentialName ?? "").Length > 200
            || (policy.Backend.PolicySigningPublicKeyPem ?? "").Length > 20000)
            return "PolicyStringFieldTooLong";
        return null;
    }

    private static string? NormalizeList(List<string>? values, int maximumCount, int maximumLength, string fieldName)
    {
        if (values is null) return $"{fieldName}Required";
        if (values.Count > maximumCount) return $"{fieldName}TooManyValues";
        for (var index = values.Count - 1; index >= 0; index--)
        {
            var value = (values[index] ?? "").Trim();
            if (value.Length > maximumLength) return $"{fieldName}ValueTooLong";
            if (value.Length == 0) values.RemoveAt(index);
            else values[index] = value;
        }
        return null;
    }
}
