namespace CompanyDlp.Service;

// Shared by FileInventoryScanner and DesktopAppProvenanceMonitor - both resolve
// policy.FileClassification.WatchedFolders to real paths from a service that runs as LocalSystem.
// Originally private to FileInventoryScanner; extracted here rather than duplicated so the fix below
// can never quietly drift out of sync between the two call sites.
public static class WatchedFolderPathResolver
{
    private const string UserProfileToken = "%USERPROFILE%";

    // Root-caused live 2026-08-26: this service runs as LocalSystem, and
    // Environment.ExpandEnvironmentVariables resolves %USERPROFILE% against the CURRENT PROCESS's own
    // environment - for LocalSystem that's C:\Windows\System32\config\systemprofile, not the logged-in
    // employee's C:\Users\<name>. Every watched folder would otherwise expand to a profile with no
    // Desktop/Documents/Downloads at all, silently, forever - a nonexistent watched folder is a normal,
    // silently-skipped case, not a failure, so this went undetected until confirmed live via temporary
    // Warning-level logging. Only %USERPROFILE% itself gets the interactive-user substitution below -
    // any other environment variable a WatchedFolders entry might use still goes through normal
    // machine/service-account expansion, correct for anything that isn't specifically "this employee's
    // own profile".
    public static string ExpandWatchedFolderPath(string folder, string? interactiveProfilePath)
    {
        if (!string.IsNullOrEmpty(interactiveProfilePath) &&
            folder.StartsWith(UserProfileToken, StringComparison.OrdinalIgnoreCase))
        {
            return interactiveProfilePath + folder[UserProfileToken.Length..];
        }

        return Environment.ExpandEnvironmentVariables(folder);
    }

    // HKLM\...\ProfileList\<SID>\ProfileImagePath is the same place Windows Explorer itself resolves a
    // user's profile directory from - correct regardless of the account's actual folder name (which
    // doesn't always match the username, e.g. renamed accounts or name collisions get a suffix).
    // Returns null (not a throw) for "no interactive user right now" (locked/logged-off session) or
    // "SID not found" - both are legitimate, common states callers must degrade out of gracefully
    // rather than blow up over.
    public static string? ResolveInteractiveUserProfilePath(string? userSid, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(userSid)) return null;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{userSid}");
            return key?.GetValue("ProfileImagePath") as string;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not resolve the interactive user's profile path from SID {Sid}.", userSid);
            return null;
        }
    }
}
