using Microsoft.Win32;

namespace CompanyDlp.Service;

// Extracted out of the now-removed AppLocker-era CliEnforcementHealthChecker (see
// CliExecutionPolicyManager's class comment for why CLI enforcement no longer uses AppLocker at all).
// This piece never had anything to do with AppLocker itself - it's a plain registry read of the
// installed Windows edition, used by HeartbeatWorker to report OperatingSystemEdition to the backend -
// so it survives as its own small utility instead of being deleted along with the rest of that file.
public static class WindowsEditionReader
{
    public static (string EditionId, string DisplayName) Read()
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
}
