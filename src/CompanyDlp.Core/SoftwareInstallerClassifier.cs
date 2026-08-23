using CompanyDlp.Contracts;

namespace CompanyDlp.Core;

/// <summary>
/// Classifies a newly-started interactive process as an installation attempt.
/// The classifier is intentionally conservative: Windows servicing processes and
/// pre-existing processes are not user installation attempts. Definitive Production
/// enforcement remains delegated to WDAC/App Control and its Code Integrity events.
/// </summary>
public static class SoftwareInstallerClassifier
{
    private static readonly string[] PackageExtensions = [".msi", ".msp", ".mst"];
    private static readonly string[] AppPackageExtensions = [".msix", ".msixbundle", ".appx", ".appxbundle"];

    public static InstallerClassification Classify(
        SoftwareProcessDescriptor process,
        SoftwarePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(policy);

        var executableName = Path.GetFileNameWithoutExtension(process.ProcessName).Trim().ToLowerInvariant();
        var executableExtension = Path.GetExtension(process.ExecutablePath).ToLowerInvariant();
        var commandLine = process.CommandLine.ToLowerInvariant();

        if (process.WindowsSessionId <= 0)
        {
            return InstallerClassification.NotInstaller("NonInteractiveSession");
        }

        if (string.IsNullOrWhiteSpace(executableName))
        {
            return InstallerClassification.NotInstaller("MissingProcessName");
        }

        // Package engines are meaningful even when they are Microsoft-signed Windows binaries.
        if (policy.BlockMsi && executableName == "msiexec")
        {
            return InstallerClassification.Installer("MsiExec");
        }

        if (policy.BlockMsixAppx && executableName is "appinstaller" or "winget")
        {
            return InstallerClassification.Installer("WindowsPackageInstaller");
        }

        if (policy.BlockMsi && ContainsPackageArgument(commandLine, PackageExtensions))
        {
            return InstallerClassification.Installer("MsiPackageArgument");
        }

        if (policy.BlockMsixAppx && ContainsPackageArgument(commandLine, AppPackageExtensions))
        {
            return InstallerClassification.Installer("AppPackageArgument");
        }

        if (!policy.BlockKnownInstallers || executableExtension != ".exe")
        {
            return InstallerClassification.NotInstaller("KnownInstallerDetectionDisabledOrUnsupportedFile");
        }

        // Windows servicing/background processes such as TrustedInstaller and
        // InstallAgentUserBroker are not user-initiated installer executables.
        if (IsMicrosoftWindowsSystemProcess(process))
        {
            return InstallerClassification.NotInstaller("MicrosoftWindowsSystemProcess");
        }

        if (executableName is "setup" or "installer" or "install")
        {
            return InstallerClassification.Installer("ExactInstallerName");
        }

        if (executableName.EndsWith("setup", StringComparison.Ordinal)
            || executableName.EndsWith("installer", StringComparison.Ordinal)
            || ContainsDelimitedInstallerToken(executableName))
        {
            return InstallerClassification.Installer("InstallerNamePattern");
        }

        return InstallerClassification.NotInstaller("NoInstallerSignal");
    }

    // Was previously a raw commandLine.Contains(extension) over the whole string - matched the
    // extension as a substring ANYWHERE, not just as a real file extension at the end of a path/token.
    // Confirmed live 2026-08-17: backgroundTaskHost.exe's legitimate Windows-internal invocation
    // ("-ServerName:App.AppX3yypww7qrft4zqhh57xaatcrnp803bj7.mca", spawned continuously for built-in
    // UWP extensions like Desktop Spotlight) contains the literal substring ".appx" purely as part of
    // an auto-generated package-family GUID ("App.AppX...") - Contains(".appx") matched it every time,
    // producing 62+ false "Software Install" audit events for one benign system process and zero signal
    // for real installers. Tokenizing on whitespace and checking EndsWith per token fixes this: a real
    // installer argument (quoted or not) always has the extension as the actual suffix of its path/
    // filename token, while this false-positive case has other characters (".mca") after it in the
    // same token.
    private static bool ContainsPackageArgument(string commandLine, IEnumerable<string> extensions)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return false;

        foreach (var rawToken in commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = rawToken.Trim('"', '\'');
            if (extensions.Any(extension => token.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsDelimitedInstallerToken(string executableName)
    {
        var tokens = executableName.Split(['-', '_', '.'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Any(token => token is "setup" or "installer" or "install");
    }

    private static bool IsMicrosoftWindowsSystemProcess(SoftwareProcessDescriptor process)
    {
        if (string.IsNullOrWhiteSpace(process.ExecutablePath)) return false;

        var windowsPath = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windowsPath)) windowsPath = @"C:\Windows";

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(process.ExecutablePath);
        }
        catch
        {
            return false;
        }

        var underWindows = fullPath.StartsWith(
            Path.GetFullPath(windowsPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

        if (!underWindows) return false;

        return process.Publisher.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)
            || process.ProcessName.Equals("TrustedInstaller.exe", StringComparison.OrdinalIgnoreCase)
            || process.ProcessName.Equals("InstallAgent.exe", StringComparison.OrdinalIgnoreCase)
            || process.ProcessName.Equals("InstallAgentUserBroker.exe", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record SoftwareProcessDescriptor(
    string ProcessName,
    string ExecutablePath,
    string CommandLine,
    string Publisher,
    int WindowsSessionId);

public sealed record InstallerClassification(bool IsInstaller, string DetectionReason)
{
    public static InstallerClassification Installer(string reason) => new(true, reason);
    public static InstallerClassification NotInstaller(string reason) => new(false, reason);
}
