using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;
using CompanyDlp.Contracts;
using CompanyDlp.Core;

namespace CompanyDlp.Service;

// ActionKeys.CliSensitiveCommand: content classification, independent of the CliExecute gate above -
// polls two sources so both PowerShell (script block text, catching -EncodedCommand/obfuscation the
// raw command line alone would miss) and cmd.exe (command line from process-creation auditing) are
// covered, classifies each against CliCommandClassifier, and writes an audit event only on an actual
// match - same "only log when something was actually found" shape PipeServer's ClassifyText handler
// already uses for ClipboardCopySensitive. EnableCommandAuditingPrerequisites() in
// CliExecutionPolicyManager is what turns these two event sources on in the first place; this monitor
// is a no-op (nothing to read) until that has run at least once.
public sealed partial class CliSensitiveCommandMonitor(
    PolicyStore policyStore,
    InteractiveUserContextProvider interactiveUserContextProvider,
    AuditLogger auditLogger,
    ILogger<CliSensitiveCommandMonitor> logger)
{
    private readonly HashSet<long> _seenPowerShellRecordIds = [];
    private readonly HashSet<long> _seenSecurityRecordIds = [];
    private DateTimeOffset _nextScanAtUtc;

    public async Task TickAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return;
        var now = DateTimeOffset.UtcNow;
        if (now < _nextScanAtUtc) return;
        _nextScanAtUtc = now.AddSeconds(2);

        var policy = policyStore.Get();
        if (!policy.Enabled || !policy.Cli.Enabled || !policy.Cli.SensitiveCommandDetectionEnabled) return;

        await ScanPowerShellScriptBlocksAsync(cancellationToken);
        await ScanCmdProcessCreationEventsAsync(cancellationToken);
    }

    private async Task ScanPowerShellScriptBlocksAsync(CancellationToken cancellationToken)
    {
        try
        {
            var query = new EventLogQuery(
                "Microsoft-Windows-PowerShell/Operational",
                PathType.LogName,
                "*[System[(EventID=4104) and TimeCreated[timediff(@SystemTime) <= 15000]]]")
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
                if (recordId > 0 && !_seenPowerShellRecordIds.Add(recordId)) continue;

                var description = SafeDescription(record);
                var scriptText = ScriptBlockTextRegex().Match(description) is { Success: true } scriptMatch
                    ? scriptMatch.Groups[1].Value
                    : description;

                await ClassifyAndAuditAsync(scriptText, "PowerShellScriptBlockLogging", recordId, cancellationToken);
            }

            if (_seenPowerShellRecordIds.Count > 5000) _seenPowerShellRecordIds.Clear();
        }
        catch (EventLogNotFoundException exception)
        {
            logger.LogDebug(exception, "PowerShell Operational log is unavailable.");
        }
        catch (UnauthorizedAccessException exception)
        {
            logger.LogWarning(exception, "Company DLP cannot read the PowerShell Operational log.");
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "PowerShell script block scan failed.");
        }
    }

    private async Task ScanCmdProcessCreationEventsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var query = new EventLogQuery(
                "Security",
                PathType.LogName,
                "*[System[(EventID=4688) and TimeCreated[timediff(@SystemTime) <= 15000]]]")
            {
                ReverseDirection = true,
                TolerateQueryErrors = true
            };

            using var reader = new EventLogReader(query);
            for (var count = 0; count < 200; count++)
            {
                using var record = reader.ReadEvent();
                if (record is null) break;
                var recordId = record.RecordId ?? 0;
                if (recordId > 0 && !_seenSecurityRecordIds.Add(recordId)) continue;

                var description = SafeDescription(record);
                var newProcessName = NewProcessNameRegex().Match(description).Groups[1].Value.Trim();
                if (!newProcessName.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase)) continue;

                var commandLine = CommandLineRegex().Match(description).Groups[1].Value.Trim();
                if (string.IsNullOrWhiteSpace(commandLine)) continue;

                await ClassifyAndAuditAsync(commandLine, "SecurityProcessCreation", recordId, cancellationToken);
            }

            if (_seenSecurityRecordIds.Count > 5000) _seenSecurityRecordIds.Clear();
        }
        catch (EventLogNotFoundException exception)
        {
            logger.LogDebug(exception, "Security log is unavailable.");
        }
        catch (UnauthorizedAccessException exception)
        {
            logger.LogWarning(exception, "Company DLP cannot read the Security log (requires \"Include command line\" auditing).");
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "cmd.exe process-creation scan failed.");
        }
    }

    private async Task ClassifyAndAuditAsync(string commandText, string method, long recordId, CancellationToken cancellationToken)
    {
        var match = CliCommandClassifier.Classify(commandText);
        if (match is null) return;

        var context = interactiveUserContextProvider.GetActiveConsoleUser();
        await auditLogger.WriteAsync(new AuditEvent
        {
            ActionKey = ActionKeys.CliSensitiveCommand,
            EventType = "CliSensitiveCommandDetected",
            Action = "sensitive-command-detected",
            Method = method,
            Result = "audited",
            ReasonCode = match.RuleId,
            RuleId = match.RuleId,
            Details = $"category={match.Category}; recordId={recordId}; preview={Mask(commandText)}",
        }, context, cancellationToken);
    }

    // Never write the raw command text to the audit trail (may itself contain secrets/credentials -
    // several of the rules above match specifically because a command references one). A short,
    // length-capped preview is enough for an admin to recognize what was flagged; the full script
    // block/command line remains in the local Windows Event Log for actual forensic follow-up.
    private static string Mask(string text)
    {
        var singleLine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= 200 ? singleLine : singleLine[..200] + "...";
    }

    private static string SafeDescription(EventRecord record)
    {
        try { return record.FormatDescription() ?? ""; }
        catch { return record.ToXml(); }
    }

    [GeneratedRegex(@"Creating Scriptblock text[^\r\n]*:\s*\r?\n(.*)", RegexOptions.Singleline)]
    private static partial Regex ScriptBlockTextRegex();

    [GeneratedRegex(@"New Process Name:\s*([^\r\n]+)")]
    private static partial Regex NewProcessNameRegex();

    [GeneratedRegex(@"Command Line:\s*([^\r\n]+)")]
    private static partial Regex CommandLineRegex();
}
