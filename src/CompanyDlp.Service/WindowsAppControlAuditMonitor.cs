using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;
using CompanyDlp.Contracts;

namespace CompanyDlp.Service;

public sealed partial class WindowsAppControlAuditMonitor(
    PolicyStore policyStore,
    InteractiveUserContextProvider interactiveUserContextProvider,
    AuditLogger auditLogger,
    ILogger<WindowsAppControlAuditMonitor> logger)
{
    private readonly HashSet<long> _seenRecordIds = [];
    private DateTimeOffset _nextScanAtUtc;

    public async Task TickAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return;
        var now = DateTimeOffset.UtcNow;
        if (now < _nextScanAtUtc) return;
        _nextScanAtUtc = now.AddSeconds(2);

        var policy = policyStore.Get();
        if (!policy.Enabled
            || !policy.Software.Enabled
            || !policy.Software.EnforcementMode.Equals("WindowsAppControl", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            // 3077/3033 are Enforced-mode block events (already handled below). 3076 is WDAC's own
            // native Audit Mode signal - "this would have been blocked if Enforced" - the standard,
            // Microsoft-recommended safe first step before ever switching a WDAC policy to Enforced.
            // Listening for it here is prep only: nothing currently deploys or activates a WDAC policy
            // (Audit or Enforced) from this repository - see docs/KNOWN_LIMITATIONS.md - so this event
            // will simply never fire until an admin deploys one separately, exactly like 3077/3033 today.
            var query = new EventLogQuery(
                "Microsoft-Windows-CodeIntegrity/Operational",
                PathType.LogName,
                "*[System[(EventID=3076 or EventID=3077 or EventID=3033) and TimeCreated[timediff(@SystemTime) <= 15000]]]")
            {
                ReverseDirection = true,
                TolerateQueryErrors = true
            };

            using var reader = new EventLogReader(query);
            for (var count = 0; count < 100; count++)
            {
                using var record = reader.ReadEvent();
                if (record is null) break;
                var recordId = record.RecordId ?? 0;
                if (recordId > 0 && !_seenRecordIds.Add(recordId)) continue;

                var description = SafeDescription(record);
                var paths = ExecutablePathRegex().Matches(description)
                    .Cast<Match>()
                    .Select(match => match.Value.Trim('"', '\'', ' ', '\r', '\n'))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var blockedPath = paths.LastOrDefault() ?? "";
                var sourcePath = paths.Count > 1 ? paths[0] : "";
                var policyId = PolicyIdRegex().Match(description).Groups[1].Value;
                var context = interactiveUserContextProvider.GetActiveConsoleUser();

                // 3076 (Audit Mode) never actually stopped anything - WDAC only evaluated what it
                // WOULD have done. Result/EventType must say so plainly rather than reusing "blocked",
                // since nothing was blocked and no enforcement behavior changes here.
                await auditLogger.WriteAsync(new AuditEvent
                {
                    ActionKey = ActionKeys.SoftwareExecuteUnapproved,
                    EventType = record.Id == 3076 ? "UnapprovedExecutionAuditOnly" : "UnapprovedExecutionBlocked",
                    Action = "code-integrity-block",
                    Method = "WindowsAppControl",
                    Result = record.Id == 3076 ? "would-have-blocked" : "blocked",
                    ReasonCode = record.Id switch
                    {
                        3033 => "EnterpriseSigningLevelNotMet",
                        3076 => "WindowsAppControlAuditModeWouldDeny",
                        _ => "WindowsAppControlPolicyDenied"
                    },
                    SourceProcessName = Path.GetFileName(sourcePath),
                    SourceProcessPath = sourcePath,
                    ResourceName = Path.GetFileName(blockedPath),
                    ResourceExtension = Path.GetExtension(blockedPath),
                    Details = $"eventId={record.Id}; recordId={recordId}; policyId={policyId}",
                }, context, cancellationToken);
            }

            if (_seenRecordIds.Count > 5000)
                _seenRecordIds.Clear();
        }
        catch (EventLogNotFoundException exception)
        {
            logger.LogDebug(exception, "Windows Code Integrity operational log is unavailable.");
        }
        catch (UnauthorizedAccessException exception)
        {
            logger.LogWarning(exception, "Company DLP cannot read the Windows Code Integrity operational log.");
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Windows App Control audit scan failed.");
        }
    }

    private static string SafeDescription(EventRecord record)
    {
        try { return record.FormatDescription() ?? ""; }
        catch { return record.ToXml(); }
    }

    [GeneratedRegex("""[A-Za-z]:\\[^\r\n"<>|]+?\.(?:exe|dll|msi|msix|appx|ps1|bat|cmd)""", RegexOptions.IgnoreCase)]
    private static partial Regex ExecutablePathRegex();

    [GeneratedRegex(@"Policy\s+ID:\s*\{?([0-9a-fA-F-]{36})\}?", RegexOptions.IgnoreCase)]
    private static partial Regex PolicyIdRegex();
}
