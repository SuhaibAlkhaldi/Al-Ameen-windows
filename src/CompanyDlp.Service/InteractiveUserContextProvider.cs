using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using CompanyDlp.Contracts;

namespace CompanyDlp.Service;

public sealed class InteractiveUserContextProvider(ILogger<InteractiveUserContextProvider> logger)
{
    // Field evidence (2026-08-16, DESKTOP-M5K7SVM): 12 of 26 CompanyDlp.Service.exe threads observed
    // stuck in Wait/EventPairLow (the classic signature of a thread blocked on an LPC/RPC call into a
    // Windows subsystem - here, WMI/winmgmt) after a fresh install, with PolicySyncWorker (and
    // eventually every other worker, via thread-pool starvation once enough threads pile up stuck the
    // same way) going completely silent - no success or failure logged, ever, despite the process
    // staying alive. Root cause: the WMI query below used to run with no timeout at all, called
    // synchronously (not via Task.Run) from GetActiveConsoleUser(), which itself is called directly
    // (not awaited around) from PolicySyncWorker and several other components every cycle. If WMI is
    // slow/unresponsive - plausible on a machine with several local accounts that get switched between
    // a lot - the calling async loop's thread blocks forever with zero exception ever thrown, hence zero
    // log line ever written. Two layers of defense now: EnumerationOptions.Timeout bounds the WMI
    // enumeration itself, and the outer Task.Run + Wait(timeout) is a hard backstop that guarantees this
    // method returns within WmiQueryTimeout no matter what WMI does internally - the Task.Run thread may
    // still leak/stay blocked in the pathological case, but the CALLER (and therefore the retry loop) is
    // never blocked past the timeout, which is what actually matters for keeping sync/heartbeat alive.
    private static readonly TimeSpan WmiQueryTimeout = TimeSpan.FromSeconds(5);

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
            var task = Task.Run(() =>
            {
                using var searcher = new ManagementObjectSearcher("SELECT UserName FROM Win32_ComputerSystem");
                searcher.Options.ReturnImmediately = true;
                searcher.Options.Timeout = WmiQueryTimeout;
                foreach (ManagementObject item in searcher.Get())
                    return item["UserName"]?.ToString() ?? "";
                return "";
            });

            if (task.Wait(WmiQueryTimeout))
                return task.Result;

            logger.LogWarning(
                "WMI query for the active interactive user did not return within {TimeoutSeconds}s; treating as unknown for this cycle instead of blocking indefinitely.",
                WmiQueryTimeout.TotalSeconds);
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
