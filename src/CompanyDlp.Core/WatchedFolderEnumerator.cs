using Microsoft.Extensions.Logging;

namespace CompanyDlp.Core;

// Extracted from FileInventoryScanner (originally private there) so the new File Inventory Report
// sync runners (FileInventoryInitialSyncRunner, FileInventoryReconciliationRunner) can walk the same
// watched folders with the exact same exclusion/error-tolerance behavior, instead of duplicating it a
// second time - see WatchedFolderPathResolver's comment for the same "extract, don't duplicate"
// reasoning applied earlier to the %USERPROFILE% expansion logic.
public static class WatchedFolderEnumerator
{
    // Confirmed live: a Desktop containing a full dev repo (source control internals, multiple
    // bin/obj build outputs, a Visual Studio cache folder) put roughly 950,000 files under one
    // watched folder, burying the user's own documents behind an enormous, permanently-growing pile
    // of files that could never be anything but Unsupported/irrelevant. Skipping these directories
    // entirely avoids the per-file stat cost and keeps a single walk from taking hours.
    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules",
        ".git",
        "bin",
        "obj",
        ".vs",
        "dist",
        "build"
    };

    public static bool IsInsideExcludedDirectory(string path)
    {
        return path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => ExcludedDirectoryNames.Contains(segment));
    }

    // Tolerates enumeration failures mid-walk (a file deleted between being listed and being touched,
    // a permission-denied subfolder, etc.) by logging and stopping THIS root's enumeration rather than
    // throwing - a single bad folder must never abort the whole watched-folder walk.
    public static IEnumerable<string> EnumerateFilesSafely(string root, ILogger logger)
    {
        IEnumerator<string>? enumerator = null;
        try
        {
            enumerator = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).GetEnumerator();
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Unable to enumerate {Root} for the watched folder walk.", root);
        }

        if (enumerator is null) yield break;

        using (enumerator)
        {
            while (true)
            {
                string current;
                try
                {
                    if (!enumerator.MoveNext()) yield break;
                    current = enumerator.Current;
                }
                catch (Exception exception)
                {
                    logger.LogDebug(exception, "Stopped enumerating {Root} for the watched folder walk.", root);
                    yield break;
                }

                if (!IsInsideExcludedDirectory(current)) yield return current;
            }
        }
    }
}
