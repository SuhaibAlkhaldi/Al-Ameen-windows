using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using CompanyDlp.Contracts;

namespace CompanyDlp.Service;

public sealed class InteractiveUserContextProvider(ILogger<InteractiveUserContextProvider> logger)
{
    public ClientContext GetActiveConsoleUser()
    {
        var username = TryGetInteractiveUsername();
        var sid = TryTranslateSid(username);
        return new ClientContext
        {
            UserSid = sid,
            Username = username,
            MachineName = Environment.MachineName,
            WindowsSessionId = OperatingSystem.IsWindows() ? unchecked((int)WTSGetActiveConsoleSessionId()) : 0,
            ClientName = "windows-service",
            ClientVersion = "1.0.0"
        };
    }

    private string TryGetInteractiveUsername()
    {
        if (!OperatingSystem.IsWindows()) return "";
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT UserName FROM Win32_ComputerSystem");
            foreach (ManagementObject item in searcher.Get())
                return item["UserName"]?.ToString() ?? "";
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not resolve the active interactive Windows user.");
        }
        return "";
    }

    private static string TryTranslateSid(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return "";
        try
        {
            return ((SecurityIdentifier)new NTAccount(username).Translate(typeof(SecurityIdentifier))).Value;
        }
        catch { return ""; }
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();
}
