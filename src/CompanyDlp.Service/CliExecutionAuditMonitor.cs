using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;
using CompanyDlp.Contracts;

namespace CompanyDlp.Service;

// Polls the AppLocker EXE rule collection's own event log for block events (Event ID 8004 =
// "was not allowed to run"), the same shape WindowsAppControlAuditMonitor already uses for the
// Code Integrity/WDAC log - a separate log/event id pair, so this is a sibling monitor rather than a
// fork of that one, per the instruction not to fold unrelated logic into it.
public sealed partial class CliExecutionAuditMonitor(
    PolicyStore policyStore,
    InteractiveUserContextProvider interactiveUserContextProvider,
    AuditLogger auditLogger,
    ILogger<CliExecutionAuditMonitor> logger)
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
            || !policy.Cli.Enabled
            || !policy.Cli.EnforcementMode.Equals(CliEnforcementModes.AppLocker, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            var query = new EventLogQuery(
                "Microsoft-Windows-AppLocker/EXE and DLL",
                PathType.LogName,
                "*[System[(EventID=8004) and TimeCreated[timediff(@SystemTime) <= 15000]]]")
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
                var blockedPath = FilePathRegex().Match(description).Groups[1].Value;
                var context = interactiveUserContextProvider.GetActiveConsoleUser();

                await auditLogger.WriteAsync(new AuditEvent
                {
                    ActionKey = ActionKeys.CliExecute,
                    EventType = "CliExecutionBlocked",
                    Action = "applocker-block",
                    Method = "AppLocker",
                    Result = "blocked",
                    ReasonCode = "AppLockerPolicyDenied",
                    ResourceName = Path.GetFileName(blockedPath),
                    ResourceExtension = Path.GetExtension(blockedPath),
                    Details = $"eventId=8004; recordId={recordId}; path={blockedPath}",
                }, context, cancellationToken);
            }

            if (_seenRecordIds.Count > 5000)
                _seenRecordIds.Clear();
        }
        catch (EventLogNotFoundException exception)
        {
            logger.LogDebug(exception, "AppLocker EXE/DLL operational log is unavailable.");
        }
        catch (UnauthorizedAccessException exception)
        {
            logger.LogWarning(exception, "Company DLP cannot read the AppLocker EXE/DLL operational log.");
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "AppLocker CLI execution audit scan failed.");
        }
    }

    private static string SafeDescription(EventRecord record)
    {
        try { return record.FormatDescription() ?? ""; }
        catch { return record.ToXml(); }
    }

    // AppLocker's 8004 message body reads e.g. "%OSDRIVE%\Windows\System32\cmd.exe was prevented from
    // running." - the path is always at the start of the description, up to the first " was ".
    [GeneratedRegex(@"^\s*(.+?)\s+was\s", RegexOptions.IgnoreCase)]
    private static partial Regex FilePathRegex();
}
