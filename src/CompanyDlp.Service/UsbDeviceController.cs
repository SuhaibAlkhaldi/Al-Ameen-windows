using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CompanyDlp.Service;

public sealed class UsbDeviceController(ILogger<UsbDeviceController> logger)
{
    public async Task<bool> DisableAsync(string rootInstanceId, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(rootInstanceId)) return false;

        var sanitizedInstanceId = rootInstanceId.Replace("\"", string.Empty, StringComparison.Ordinal);
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "pnputil.exe"),
            Arguments = $"/disable-device \"{sanitizedInstanceId}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return false;

            var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stdOut = await stdOutTask;
            var stdErr = await stdErrTask;

            // Confirmed live against real hardware: pnputil /disable-device returns exit code 0 even when
            // it fails outright (e.g. "Access is denied" because the calling process lacks sufficient
            // privilege) - the failure is only visible in its text output. Trusting the exit code alone
            // previously produced a false-positive "blocked" audit event while the device stayed fully
            // functional. Verify the device's actual resulting state via the same CM_* device-node APIs
            // UsbDeviceInventory already uses, instead of trusting pnputil's exit code or parsing its text.
            var actuallyDisabled = IsDeviceDisabled(sanitizedInstanceId);
            if (!actuallyDisabled)
            {
                logger.LogWarning(
                    "pnputil exited with code {ExitCode} for {RootId} but the device is not actually disabled. Stdout: {StdOut} Stderr: {StdErr}",
                    process.ExitCode, rootInstanceId, stdOut.Trim(), stdErr.Trim());
            }

            return actuallyDisabled;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to disable USB device {RootId}", rootInstanceId);
            return false;
        }
    }

    private static bool IsDeviceDisabled(string instanceId)
    {
        if (CM_Locate_DevNodeW(out var devInst, instanceId, 0) != 0) return false;
        if (CM_Get_DevNode_Status(out var status, out var problemNumber, devInst, 0) != 0) return false;

        const uint DN_HAS_PROBLEM = 0x00000400;
        const uint CM_PROB_DISABLED = 22;
        return (status & DN_HAS_PROBLEM) != 0 && problemNumber == CM_PROB_DISABLED;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_Status(out uint status, out uint problemNumber, uint devInst, uint flags);
}
