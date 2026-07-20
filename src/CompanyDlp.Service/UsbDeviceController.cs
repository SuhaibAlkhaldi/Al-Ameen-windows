using System.Diagnostics;

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
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode == 0) return true;

            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            logger.LogWarning("pnputil failed to disable {RootId}: {Error}", rootInstanceId, error);
            return false;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to disable USB device {RootId}", rootInstanceId);
            return false;
        }
    }
}
