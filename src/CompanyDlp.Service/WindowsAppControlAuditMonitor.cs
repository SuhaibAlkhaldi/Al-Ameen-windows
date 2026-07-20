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
            var query = new EventLogQuery(
                "Microsoft-Windows-CodeIntegrity/Operational",
                PathType.LogName,
                "*[System[(EventID=3077 or EventID=3033) and TimeCreated[timediff(@SystemTime) <= 15000]]]")
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

                await auditLogger.WriteAsync(new AuditEvent
                {
                    ActionKey = ActionKeys.SoftwareExecuteUnapproved,
                    EventType = "UnapprovedExecutionBlocked",
                    Action = "code-integrity-block",
                    Method = "WindowsAppControl",
                    Result = "blocked",
                    ReasonCode = record.Id == 3033 ? "EnterpriseSigningLevelNotMet" : "WindowsAppControlPolicyDenied",
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
