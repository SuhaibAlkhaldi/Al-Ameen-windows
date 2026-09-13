using System.Security.Cryptography;
using CompanyDlp.Contracts;

namespace CompanyDlp.Service;

// Answers "does this newly-discovered local file's content match something that was on a removable
// drive?" for the file.open-access USB channel - see ActionKeys.FileOpenAccess's comment and
// FileProvenanceStore for the full design.
//
// Deliberately snapshot-at-connect-time rather than compare-live-at-discovery-time: a live comparison
// (walk whatever removable drives happen to be connected right now) has a real race - a file copied
// and the drive ejected before FileInventoryScanner's next tick would find no drive to compare
// against and be misclassified self-created. Snapshotting a drive's content the moment it's detected
// connected, and retaining that snapshot in memory for a while after the drive disconnects, closes
// that window: MatchesAnySnapshot below checks every retained snapshot, not just currently-connected
// drives.
//
// Uses System.IO.DriveInfo (DriveType.Removable) rather than correlating UsbDeviceInventory's PnP/WMI
// device bundles to a volume/drive letter - deliberately avoided as unnecessary complexity (see the
// plan's design notes); this class doesn't need to know WHICH USB device a drive letter belongs to,
// only that it is a removable volume at all.
public sealed class UsbSnapshotCache(PolicyStore policyStore, ILogger<UsbSnapshotCache> logger)
{
    private readonly object _sync = new();
    private readonly Dictionary<string, DriveSnapshot> _snapshotsByRoot = new(StringComparer.OrdinalIgnoreCase);

    private sealed class DriveSnapshot
    {
        public DateTimeOffset LastSeenConnectedUtc;
        public HashSet<string> ContentHashes = new(StringComparer.OrdinalIgnoreCase);
    }

    public bool IsAnyDriveCurrentlyConnected { get; private set; }

    // Called on the same cadence as FileInventoryScanner's tick (or more often) - cheap when no
    // removable drive is connected (just DriveInfo.GetDrives() and a dictionary sweep).
    public void Tick()
    {
        var policy = policyStore.Get().FileOpenProtection;
        var now = DateTimeOffset.UtcNow;

        List<DriveInfo> connected;
        try
        {
            connected = DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Removable && drive.IsReady).ToList();
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not enumerate removable drives this tick.");
            connected = [];
        }

        lock (_sync)
        {
            IsAnyDriveCurrentlyConnected = connected.Count > 0;

            foreach (var drive in connected)
            {
                if (_snapshotsByRoot.TryGetValue(drive.RootDirectory.FullName, out var existing))
                {
                    existing.LastSeenConnectedUtc = now;
                    continue;
                }

                // First time seeing this drive connected - snapshot it now, once, rather than
                // re-walking it every tick while it stays connected (content on a removable drive
                // essentially never changes while it's just sitting there plugged in for our
                // purposes; re-walking it repeatedly would only add cost with no real benefit).
                var snapshot = new DriveSnapshot { LastSeenConnectedUtc = now };
                TrySnapshotDrive(drive, snapshot, policy);
                _snapshotsByRoot[drive.RootDirectory.FullName] = snapshot;
            }

            var retention = TimeSpan.FromMinutes(Math.Max(1, policy.UsbSnapshotRetentionMinutes));
            foreach (var root in _snapshotsByRoot.Keys.Where(root => now - _snapshotsByRoot[root].LastSeenConnectedUtc > retention).ToList())
            {
                _snapshotsByRoot.Remove(root);
            }
        }
    }

    public bool MatchesAnySnapshot(string contentHash)
    {
        lock (_sync)
        {
            return _snapshotsByRoot.Values.Any(snapshot => snapshot.ContentHashes.Contains(contentHash));
        }
    }

    private void TrySnapshotDrive(DriveInfo drive, DriveSnapshot snapshot, FileOpenProtectionPolicy policy)
    {
        try
        {
            long totalBytes = 0;
            var fileCount = 0;
            foreach (var path in EnumerateFilesSafely(drive.RootDirectory.FullName))
            {
                if (fileCount >= policy.UsbSnapshotMaxFiles || totalBytes >= policy.UsbSnapshotMaxTotalBytes) break;

                FileInfo info;
                try { info = new FileInfo(path); }
                catch { continue; }
                if (!info.Exists) continue;

                fileCount++;
                totalBytes += info.Length;

                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                    snapshot.ContentHashes.Add(hash);
                }
                catch
                {
                    // Unreadable file (locked, permissions, transient I/O) - just skip it; a file we
                    // can't hash here can't be matched against later either way.
                }
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not snapshot removable drive {Drive}; files copied from it may not be recognized as external.", drive.Name);
        }
    }

    private static IEnumerable<string> EnumerateFilesSafely(string root)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);
        }
        catch
        {
            yield break;
        }

        using var enumerator = files.GetEnumerator();
        while (true)
        {
            string current;
            try
            {
                if (!enumerator.MoveNext()) break;
                current = enumerator.Current;
            }
            catch
            {
                // A subdirectory became inaccessible/removed mid-walk - stop rather than risk an
                // infinite retry loop; whatever was already yielded is still a useful partial snapshot.
                break;
            }
            yield return current;
        }
    }
}
