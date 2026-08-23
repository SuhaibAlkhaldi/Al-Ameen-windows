using System.Collections.Concurrent;
using System.Management;
using System.Security.Cryptography;
using CompanyDlp.Contracts;
using CompanyDlp.Core;

namespace CompanyDlp.Service;

// Blocks printing of classified (Internal/Secret/Very Secret) files unless the user has an
// admin-granted permission - Public prints freely, same shape as every other gated action here
// (USB, screen capture/recording, uploads): detect via a Windows-native signal, resolve which
// action + classification is involved, ask PermissionEvaluator, enforce, audit, notify.
//
// Detection uses a WMI event watcher on Win32_PrintJob creation - the same System.Management
// dependency ProcessProtectionMonitor already uses for Win32_Process lookups. A job lands in the
// spool queue after the source app has rendered/spooled it but before it reaches the physical
// printer, giving a real (if brief) window to cancel via Delete() if not permitted. This is a
// known, accepted limitation (confirmed with the user) rather than instant/page-0 blocking - a
// fully bulletproof solution would need a native Print Processor/Port Monitor DLL, a much larger
// separate effort out of scope here. That window is spent entirely on our own reaction latency, so
// this class is a dedicated BackgroundService (not ticked from DlpWorker's shared, unrelated
// screen-recording-poll-interval loop) woken immediately by a semaphore the WMI callback releases,
// rather than waiting for a shared polling cadence to get around to it - confirmed live
// (2026-08-23) that the ~1.25s combined latency of a 1-second WMI WITHIN clause plus a 250ms shared
// tick delay was enough for this printer to already be mid-print by the time Delete() ran.
//
// Resolving which file is being printed: Windows print jobs do not reliably carry the original
// source file's full path - Win32_PrintJob.Document is just the job's display title, which varies
// by application. Two signals, in priority order, both explained in PrintProtectionMonitor's plan:
//   1. Parse the job title for one of FilenameClassificationTagger's own tag strings (e.g.
//      "[Secret]") - most classified files already carry this in their filename, and many
//      applications' print-job titles echo the source filename verbatim or near-verbatim. Reuses
//      FilenameClassificationTagger directly so the two can never drift apart.
//   2. Fallback: if the title looks like a real, existing file path, hash it and let
//      PermissionEvaluator resolve the tier from the classification cache the normal way - this
//      also enables exact-file-scoped grants (not just tier-scoped) when it succeeds.
//   3. Neither resolves: fail closed to VerySecret, matching every other fail-closed default in
//      this codebase (PermissionEvaluator's own cache-miss fallback, PipeServer's same choice).
public sealed class PrintProtectionMonitor(
    PolicyStore policyStore,
    AgentIdentityProvider identityProvider,
    PermissionEvaluator permissionEvaluator,
    FileClassificationCache classificationCache,
    AuditLogger auditLogger,
    NotificationStore notificationStore,
    ILogger<PrintProtectionMonitor> logger) : BackgroundService
{
    private readonly ConcurrentQueue<DetectedPrintJob> _pending = new();

    // Released by the WMI callback thread the instant a job is detected - SemaphoreSlim.Release()
    // does no I/O and is safe to call there. ExecuteAsync's WaitAsync below wakes up as soon as this
    // fires, instead of sitting idle until a shared external loop happens to poll this class again.
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private ManagementEventWatcher? _watcher;
    private bool _watcherStartAttempted;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var policy = policyStore.Get();
            if (!policy.Enabled || !policy.Print.Enabled)
            {
                StopWatcher();
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                continue;
            }

            EnsureWatcherStarted();

            try
            {
                // The 1-second timeout is just so a policy flip (Print.Enabled -> false) or a
                // watcher that failed to start gets rechecked promptly even with no jobs arriving -
                // the semaphore is what actually drives fast reaction to a real print job.
                await _signal.WaitAsync(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            while (_pending.TryDequeue(out var job))
            {
                await HandleJobAsync(job, policy, stoppingToken);
            }
        }

        StopWatcher();
    }

    private void EnsureWatcherStarted()
    {
        if (_watcherStartAttempted) return;
        _watcherStartAttempted = true;

        try
        {
            // WITHIN 0.1 (not the WMI default of 1 second) - this is WMI's own polling interval for
            // noticing the new Win32_PrintJob instance in the first place, well before this class's
            // own dispatch even begins. See the class comment: confirmed live that a 1-second value
            // here alone accounted for most of the missed cancellation window.
            _watcher = new ManagementEventWatcher(new WqlEventQuery(
                "SELECT * FROM __InstanceCreationEvent WITHIN 0.1 WHERE TargetInstance ISA 'Win32_PrintJob'"));
            _watcher.EventArrived += OnPrintJobCreated;
            _watcher.Start();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not start the print job watcher; printing will not be gated.");
            _watcher = null;
        }
    }

    private void StopWatcher()
    {
        if (_watcher is null) return;
        try
        {
            _watcher.EventArrived -= OnPrintJobCreated;
            _watcher.Stop();
            _watcher.Dispose();
        }
        catch { }
        finally
        {
            _watcher = null;
            _watcherStartAttempted = false;
        }
    }

    // Runs on a WMI callback thread - deliberately does no DI-scoped/async work here, just pulls
    // the fields we need off the event, queues them, and releases the semaphore so ExecuteAsync
    // wakes immediately on its own async context - matching how every other monitor in this class
    // keeps its detection and its evaluate/enforce/audit work on separate, predictable execution
    // contexts. Release() is a plain in-memory signal (no I/O), safe to call from this thread.
    private void OnPrintJobCreated(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var target = (ManagementBaseObject)e.NewEvent["TargetInstance"];
            _pending.Enqueue(new DetectedPrintJob(
                JobName: target["Name"]?.ToString() ?? "",
                Document: target["Document"]?.ToString() ?? "",
                Owner: target["Owner"]?.ToString() ?? ""));
            _signal.Release();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not read a newly-detected print job's details.");
        }
    }

    private async Task HandleJobAsync(DetectedPrintJob job, DlpPolicy policy, CancellationToken cancellationToken)
    {
        // Confirmed live (2026-08-23): the Document field on the __InstanceCreationEvent's
        // TargetInstance (captured in OnPrintJobCreated) is sometimes still the generic placeholder
        // "Local Downlevel Document" - the spooler hasn't finished writing the real title into
        // Win32_PrintJob yet at the exact instant the instance-creation event fires, especially now
        // that detection itself got much faster (see the class comment). A fresh, separate query by
        // JobName immediately before classifying picks up the real title if it has landed by then,
        // without slowing down detection itself - this happens after the job is already queued, not
        // before. Same instance-lifetime reasoning as TryCancelJob's own re-query: never reuse a WMI
        // object captured on the event-callback thread.
        var document = RefreshDocumentTitle(job.JobName, job.Document);
        var resolved = ResolveClassification(document, policy.FileClassification.WatchedFolders);

        var context = new ClientContext
        {
            Username = job.Owner,
            MachineName = Environment.MachineName,
            ClientName = "PrintProtectionMonitor",
            ClientVersion = "1.0.0"
        };

        var decision = permissionEvaluator.Evaluate(
            policy,
            ActionKeys.FilePrint,
            context,
            identityProvider.Get(),
            DateTimeOffset.UtcNow,
            fileHash: resolved.FileHash,
            knownClassificationTier: resolved.TaggedTier);

        var result = decision.IsAllowed ? "allowed" : "detected";
        if (!decision.IsAllowed && policy.Print.EnforcementMode.Equals("Block", StringComparison.OrdinalIgnoreCase))
        {
            result = TryCancelJob(job) ? "blocked" : "cancel-failed";
        }

        // A filename tag is authoritative when present (it's what the classification decision above
        // used too). Otherwise, mirror PipeServer's browser.upload lookup so a hash-resolved file shows
        // its real cached tier here instead of going blank - falling back to VerySecret to match the
        // same fail-closed tier the permission decision itself already applied on a cache miss.
        var resourceClassification = resolved.TaggedTier
            ?? (resolved.FileHash is not null
                ? classificationCache.TryGet(resolved.FileHash)?.Classification ?? ClassificationTiers.VerySecret
                : ClassificationTiers.VerySecret);

        var correlationId = Guid.NewGuid();
        await auditLogger.WriteAsync(new AuditEvent
        {
            CorrelationId = correlationId,
            ActionKey = ActionKeys.FilePrint,
            EventType = decision.IsAllowed ? "PrintAllowed" : result == "blocked" ? "PrintBlocked" : "PrintDetected",
            Action = "print-detected",
            Method = "PrintSpoolerWatcher",
            Result = result,
            ReasonCode = decision.IsAllowed
                ? decision.ReasonCode
                : result == "blocked" ? "PrintDeniedByPolicy"
                : result == "cancel-failed" ? "PrintCancelFailed"
                : "PrintAuditOnly",
            PermissionGrantId = decision.PermissionGrantId,
            ResourceName = resolved.ResourceName,
            ResourceExtension = resolved.Extension,
            ResourceSizeBytes = resolved.SizeBytes,
            ResourceSha256 = resolved.FileHash ?? "",
            ResourceClassification = resourceClassification,
            Details = document
        }, context, cancellationToken);

        if (decision.IsAllowed) return;

        var (title, message) = result switch
        {
            "blocked" => ($"Print blocked",
                $"\"{resolved.ResourceName}\" was not printed because its classification requires permission."),
            "cancel-failed" => ("Print could not be stopped",
                $"\"{resolved.ResourceName}\" needed permission to print, but Company DLP could not cancel the job. Contact IT."),
            _ => ("Print detected", $"\"{resolved.ResourceName}\" was detected while print protection is enabled.")
        };
        notificationStore.Add(
            "print",
            title,
            message,
            result == "cancel-failed" ? "Error" : "Warning",
            result,
            requestPermissionUrl: BuildRequestPermissionUrl(policy, correlationId));
    }

    // Mirrors the browser extension's buildRequestPermissionLink (content.js) - same query
    // parameters, same portal, just triggered from a Desktop-side event instead of a page block.
    private static string BuildRequestPermissionUrl(DlpPolicy policy, Guid correlationId)
    {
        var portalBaseUrl = policy.FileClassification.PortalBaseUrl;
        if (string.IsNullOrWhiteSpace(portalBaseUrl)) return "";

        return $"{portalBaseUrl.TrimEnd('/')}/permission-requests/new" +
            $"?actionKey={Uri.EscapeDataString(ActionKeys.FilePrint)}&fromEvent={correlationId:D}";
    }

    private readonly record struct ResolvedPrintResource(
        string? TaggedTier, string? FileHash, string ResourceName, string Extension, long? SizeBytes);

    // Tag parsing and path/hash resolution are independent signals, not alternatives: a tagged file's
    // job title (e.g. "[Secret] Report.pdf") only carries the tier, not a hash/size, so a Request
    // Permission page's "Blocked File" section (keyed by hash, same as browser.upload/browser.drag-drop
    // via PipeServer) would otherwise show nothing for every tag-resolved print. Whenever the document
    // also resolves to a real file on disk, hash/size/extension are captured regardless of whether a
    // tag was found - the tag still wins for the tier used in the permission decision and displayed
    // classification (it's the more direct signal per PermissionEvaluator's own priority), but the
    // audit record now carries full file details whenever the file is actually reachable. Priority when
    // no file is reachable: filename tag tier only, then fail-closed to VerySecret.
    private static ResolvedPrintResource ResolveClassification(string document, IReadOnlyList<string> watchedFolders)
    {
        var hasTag = FilenameClassificationTagger.TryParseTierFromTaggedText(document, out var taggedTier);

        if (LooksLikeExistingFile(document, watchedFolders, out var resolvedPath))
        {
            try
            {
                using var stream = new FileStream(resolvedPath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                return new ResolvedPrintResource(
                    hasTag ? taggedTier : null,
                    hash,
                    Path.GetFileName(resolvedPath) ?? resolvedPath!,
                    Path.GetExtension(resolvedPath) ?? "",
                    stream.Length);
            }
            catch
            {
                // Falls through to the tag (if any) or fail-closed below - a file we can see but can't
                // read (locked by the printing application, permissions, etc.) is exactly the case
                // fail-closed exists for when no tag is available either.
            }
        }

        if (hasTag)
        {
            return new ResolvedPrintResource(taggedTier, null, document, Path.GetExtension(document), null);
        }

        return new ResolvedPrintResource(
            ClassificationTiers.VerySecret, null,
            string.IsNullOrWhiteSpace(document) ? "(unknown document)" : document, "", null);
    }

    // Win32_PrintJob.Document is usually just a bare filename (see the class-level comment), so
    // File.Exists(document) alone only ever matches a full path some application happened to supply.
    // FileClassification.WatchedFolders is the same set of folders FileInventoryScanner already
    // classifies files in - checking a same-named file there too lets a print of an already-known,
    // watched file resolve its real hash/size instead of falling back to filename-tag-only or
    // fail-closed, without guessing at arbitrary locations the policy hasn't opted into.
    private static bool LooksLikeExistingFile(string document, IReadOnlyList<string> watchedFolders, out string? resolvedPath)
    {
        resolvedPath = null;
        if (string.IsNullOrWhiteSpace(document)) return false;
        if (document.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;

        if (File.Exists(document))
        {
            resolvedPath = document;
            return true;
        }

        var fileName = Path.GetFileName(document);
        if (string.IsNullOrWhiteSpace(fileName)) return false;

        foreach (var folder in watchedFolders)
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            var candidate = Path.Combine(folder, fileName);
            if (File.Exists(candidate))
            {
                resolvedPath = candidate;
                return true;
            }
        }
        return false;
    }

    // Best-effort: a fresh query that fails or comes back empty just means "no better answer than
    // what we already had" - falls back to the original event-time title rather than blocking
    // classification on this succeeding.
    private static string RefreshDocumentTitle(string jobName, string fallback)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT Document FROM Win32_PrintJob WHERE Name = '{jobName.Replace("'", "''")}'");
            using var results = searcher.Get();
            foreach (ManagementObject printJob in results)
            {
                using (printJob)
                {
                    var refreshed = printJob["Document"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(refreshed)) return refreshed;
                }
            }
        }
        catch
        {
            // Fall through to fallback below.
        }
        return fallback;
    }

    private bool TryCancelJob(DetectedPrintJob job)
    {
        // Re-queried fresh by name rather than reusing a WMI object captured on the event-callback
        // thread - COM object lifetime/marshaling across threads is fragile, and this job's Name is
        // already the unique key we need to look it back up here.
        try
        {
            var escapedName = job.JobName.Replace("'", "''");
            using var searcher = new ManagementObjectSearcher($"SELECT * FROM Win32_PrintJob WHERE Name = '{escapedName}'");
            using var results = searcher.Get();
            var found = false;
            foreach (ManagementObject printJob in results)
            {
                using (printJob)
                {
                    // Confirmed live (2026-08-23): InvokeMethod("Delete", null) throws
                    // "This method is not implemented in any class" - Win32_PrintJob has no WMI
                    // schema-defined method named "Delete" to invoke that way. Cancelling a WMI
                    // instance is a generic operation (WMI's DeleteInstance), exposed on
                    // ManagementObject as its own .Delete() method - not something to look up via
                    // InvokeMethod, which only resolves class-defined methods.
                    printJob.Delete();
                    found = true;
                }
            }
            if (!found) return true; // Already gone (finished/cancelled elsewhere) - nothing left to block.
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not cancel print job {JobName}.", job.JobName);
            return false;
        }

        // Re-verify rather than trust Delete()'s apparent success, matching UsbDeviceController's
        // CM_Get_DevNode_Status re-check after pnputil - the enforcement API returning without
        // throwing is not proof the job actually stopped.
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT Name FROM Win32_PrintJob WHERE Name = '{job.JobName.Replace("'", "''")}'");
            using var results = searcher.Get();
            return results.Count == 0;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not verify cancellation of print job {JobName}.", job.JobName);
            return false;
        }
    }

    private sealed record DetectedPrintJob(string JobName, string Document, string Owner);
}
