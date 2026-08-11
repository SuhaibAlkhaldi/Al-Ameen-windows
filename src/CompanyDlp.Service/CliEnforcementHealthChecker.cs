using System.ServiceProcess;
using Microsoft.Win32;

namespace CompanyDlp.Service;

public enum CliEnforcementHealthStatus
{
    // Platform supports AppLocker, the module/cmdlets are present, and AppIDSvc is confirmed running -
    // Deny rules will actually enforce.
    Active,

    // Neither the edition nor the patched-build exception (KB5024351) support AppLocker enforcement on
    // this device. No amount of service management fixes this; it is a hard platform limitation.
    NotSupportedPlatform,

    // Platform supports AppLocker, but the AppLocker PowerShell module itself is missing from this
    // install (e.g. a stripped-down/LTSC-IoT image, or Features on Demand removed) - Set-AppLockerPolicy
    // would fail outright, not just silently not-enforce. Deliberately a distinct status from
    // NotSupportedPlatform: cmdlet availability and policy enforcement are two different things, and an
    // admin needs to know which one is actually broken on a given device.
    ModuleUnavailable,

    // Platform and module are both fine, but the Application Identity service (AppIDSvc) - the service
    // that actually evaluates AppLocker rules - is not running and could not be started.
    ServiceNotRunning
}

public sealed record CliEnforcementHealth(
    CliEnforcementHealthStatus Status,
    string EditionId,
    string EditionDisplayName);

// Checks the real preconditions AppLocker enforcement requires that Set-AppLockerPolicy itself never
// validates - it happily accepts and writes a policy on an unsupported platform, a device missing the
// AppLocker module, or with AppIDSvc stopped, with zero indication anything is wrong. Without this
// check, an admin setting CliExecute=Block for an employee on an unsupported device sees "policy
// applied" everywhere while the employee can still open PowerShell freely - a silent-failure gap, not
// a hypothetical one, since AppIDSvc ships stopped/manual-start by default on most Windows installs.
public sealed class CliEnforcementHealthChecker(ILogger<CliEnforcementHealthChecker> logger)
{
    // Historically AppLocker enforced only on Enterprise/Education/Server editions - kept as a fallback
    // for older/unpatched machines. EditionID registry values: "Enterprise", "Education", "EnterpriseS"
    // (LTSC), "ServerStandard", "ServerDatacenter", "ServerRdsh", "IoTEnterprise".
    private static readonly string[] SupportedEditionMarkers =
        ["Enterprise", "Education", "Server", "IoTEnterprise"];

    // KB5024351 (Sept-Oct 2022 cumulative updates) removed the edition restriction entirely on
    // Windows 10 2004+/20H2/21H1+ (build 19041+) and every Windows 11 version (build 22000+, which is
    // already >= 19041, so a single threshold covers both cases) - on a patched machine, AppLocker
    // enforces regardless of edition, Pro included. This is a build-number proxy for "has that update,"
    // not a literal check of installed-update history (Windows Update history isn't reliably queryable
    // without shelling out) - given the update is now years old, a qualifying build without it would
    // mean updates have been off for years, an edge case worth documenting rather than engineering
    // around here.
    private const int MinimumBuildForUnrestrictedAppLocker = 19041;

    // The AppLocker PowerShell module ships as a fixed Windows-feature file at this path - a file-
    // existence check is a cheap, reliable proxy for cmdlet availability without shelling out to
    // PowerShell just to ask it about itself (same "registry/file read over process shell-out"
    // preference as ReadEdition below).
    private static readonly string AppLockerModulePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell", "v1.0", "Modules", "AppLocker", "AppLocker.psd1");

    public CliEnforcementHealth Check()
    {
        if (!OperatingSystem.IsWindows())
            return new CliEnforcementHealth(CliEnforcementHealthStatus.NotSupportedPlatform, "", "");

        var (editionId, displayName) = ReadEdition();

        if (!IsPlatformSupported(editionId))
            return new CliEnforcementHealth(CliEnforcementHealthStatus.NotSupportedPlatform, editionId, displayName);

        if (!IsAppLockerModuleAvailable())
            return new CliEnforcementHealth(CliEnforcementHealthStatus.ModuleUnavailable, editionId, displayName);

        var serviceRunning = EnsureAppIdSvcRunning();
        return new CliEnforcementHealth(
            serviceRunning ? CliEnforcementHealthStatus.Active : CliEnforcementHealthStatus.ServiceNotRunning,
            editionId,
            displayName);
    }

    internal static bool IsPlatformSupported(string editionId) =>
        IsEditionSupported(editionId) || IsBuildSupportedRegardlessOfEdition(Environment.OSVersion.Version.Build);

    internal static bool IsEditionSupported(string editionId) =>
        !string.IsNullOrWhiteSpace(editionId) &&
        SupportedEditionMarkers.Any(marker => editionId.Contains(marker, StringComparison.OrdinalIgnoreCase));

    internal static bool IsBuildSupportedRegardlessOfEdition(int build) =>
        build >= MinimumBuildForUnrestrictedAppLocker;

    internal static bool IsAppLockerModuleAvailable() => File.Exists(AppLockerModulePath);

    // Registry read only, deliberately not a PowerShell shell-out - cheaper, more reliable, and does
    // not depend on parsing a "Caption" string (Win32_OperatingSystem.Caption / systeminfo.exe output)
    // whose exact wording varies by locale and Windows release.
    internal static (string EditionId, string DisplayName) ReadEdition()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var editionId = key?.GetValue("EditionID") as string ?? "";
            var productName = key?.GetValue("ProductName") as string ?? "";
            return (editionId, string.IsNullOrWhiteSpace(productName) ? editionId : productName);
        }
        catch
        {
            return ("", "");
        }
    }

    // Only ever starts an already-installed, merely-stopped service (safe, non-destructive, and
    // exactly what the product needs - AppIDSvc ships "Manual" start type by default, so this is the
    // common case, not an edge case). Never touches StartType/install state, and if the service is
    // Disabled or missing entirely, Start() throws and this honestly reports the gap rather than
    // pretending success.
    private bool EnsureAppIdSvcRunning()
    {
        try
        {
            using var controller = new ServiceController("AppIDSvc");
            if (controller.Status == ServiceControllerStatus.Running) return true;

            if (controller.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)
            {
                controller.Start();
                controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                return controller.Status == ServiceControllerStatus.Running;
            }

            return false;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Could not confirm or start the Application Identity service (AppIDSvc), required for AppLocker enforcement.");
            return false;
        }
    }
}
