using System.Text.RegularExpressions;

namespace CompanyDlp.Core;

public sealed class CliCommandMatch
{
    public string RuleId { get; set; } = "";
    public string RuleName { get; set; } = "";
    public string Category { get; set; } = "";
}

// v1 rule set for ActionKeys.CliSensitiveCommand - small and deliberately not exhaustive (per the
// task brief). Same shape as ContentClassifier: named, timeout-bounded regexes, first match wins.
// Every rule here is a coarse indicator, not proof of malicious intent (e.g. curl to an internal
// company API also matches "outbound transfer") - this is a detection/audit channel, not a block
// decision, so false positives cost an admin a look at the Alerts list rather than a broken workflow.
public static class CliCommandClassifier
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    // Network-transfer tool invocation, with a bare-domain fallback (no http/https/ftp scheme, e.g.
    // "curl example.com/x") that excludes common file extensions right after the dot so an ordinary
    // filename like "out.zip" doesn't itself look like a domain and false-positive.
    private static readonly Regex OutboundTransferPattern = new(
        @"\b(curl|scp|ftp|Invoke-WebRequest|Invoke-RestMethod|iwr|irm)\b(?![^\r\n]*\b(localhost|127\.0\.0\.1|::1)\b)[^\r\n]*\b(https?://|ftp://|" +
        @"[a-z0-9](?:[a-z0-9\-]*[a-z0-9])?\.(?!zip|exe|txt|csv|json|xml|docx?|pdf|dll|ps1|psm1|bat|cmd|log|dat|bak|tmp|rar|7z|iso|msi|png|jpe?g|gif|bmp|mp[34]|avi|mkv)[a-z]{2,}\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

    // The "zip" COMMAND invocation, not the substring "zip" inside an ordinary ".zip" filename (a
    // plain \bzip\b would also match there, since "." is a non-word character and still satisfies a
    // \b word boundary either side of it).
    private static readonly Regex ArchiveToolPattern = new(
        @"\b(Compress-Archive|tar\s+-c)\b|(?<!\.)\bzip\b(?=[\s;&|]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex EncodedPayloadPattern = new(
        @"-(e|enc|encodedcommand)\b|FromBase64String|\[Convert\]::FromBase64String|-EncodedCommand\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex CredentialAccessPattern = new(
        @"\breg(\.exe)?\s+(export|save)\b[\s\S]*\b(sam|system|security)\b" +
        @"|Get-Content\b[^\r\n]*\\(config\\SAM|Windows\\NTDS|\.ssh\\id_rsa|\.aws\\credentials|\.azure\\)" +
        @"|mimikatz|lsass\.exe\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex DestructiveShadowCopyPattern = new(
        @"\bvssadmin\b[^\r\n]*\bdelete\b[^\r\n]*\bshadows\b" +
        @"|\bwmic\b[^\r\n]*\bshadowcopy\b[^\r\n]*\bdelete\b" +
        @"|Remove-Item\b[^\r\n]*-Recurse[^\r\n]*-Force[^\r\n]*\$env:",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

    // The archive-then-network rule's network-side check deliberately reuses the SAME transfer-tool
    // name list as the outbound-transfer rule, but without requiring a URL/domain - "zip then scp to
    // a bare hostname with no domain" is still exactly the pattern this rule exists to catch, and
    // OutboundTransferPattern alone would miss it for lack of a matching destination.
    private static readonly Regex TransferToolNamePattern = new(
        @"\b(curl|scp|ftp|Invoke-WebRequest|Invoke-RestMethod|iwr|irm)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly (string Id, string Name, string Category, Func<string, bool> IsMatch)[] Rules =
    [
        (
            "cli-outbound-transfer",
            "Outbound data transfer to a non-local host",
            "Exfiltration",
            text => SafeIsMatch(OutboundTransferPattern, text)
        ),
        (
            "cli-encoded-payload",
            "Base64-encoded / obfuscated command payload",
            "Obfuscation",
            text => SafeIsMatch(EncodedPayloadPattern, text)
        ),
        (
            "cli-archive-then-network",
            "Archive built then a network transfer command in the same command",
            "Exfiltration",
            text => SafeIsMatch(ArchiveToolPattern, text) && SafeIsMatch(TransferToolNamePattern, text)
        ),
        (
            "cli-credential-access",
            "Credential or secret store access",
            "CredentialAccess",
            text => SafeIsMatch(CredentialAccessPattern, text)
        ),
        (
            "cli-destructive-shadow-copy",
            "Shadow copy deletion (ransomware indicator)",
            "Destructive",
            text => SafeIsMatch(DestructiveShadowCopyPattern, text)
        ),
    ];

    private static bool SafeIsMatch(Regex pattern, string text)
    {
        try
        {
            return pattern.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    public static CliCommandMatch? Classify(string commandText)
    {
        if (string.IsNullOrWhiteSpace(commandText)) return null;

        foreach (var rule in Rules)
        {
            if (!rule.IsMatch(commandText)) continue;

            return new CliCommandMatch
            {
                RuleId = rule.Id,
                RuleName = rule.Name,
                Category = rule.Category
            };
        }

        return null;
    }
}
