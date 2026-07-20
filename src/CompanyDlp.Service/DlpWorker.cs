namespace CompanyDlp.Service;

public sealed class DlpWorker(
    PolicyStore policyStore,
    PipeServer pipeServer,
    BrowserPolicyManager browserPolicyManager,
    UsbProtectionMonitor usbMonitor,
    ProcessProtectionMonitor processMonitor,
    SoftwareProtectionMonitor softwareMonitor,
    WindowsAppControlAuditMonitor windowsAppControlAuditMonitor,
    PermissionLifecycleMonitor permissionLifecycleMonitor,
    SessionAgentSupervisor sessionAgentSupervisor,
    ILogger<DlpWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var policy = policyStore.Reload();
        logger.LogInformation("Company DLP started in {Mode} mode using {PolicyPath}", policy.Runtime.Mode, policyStore.PolicyPath);

        var pipeTask = pipeServer.RunAsync(stoppingToken);
        var lastPolicyApply = DateTimeOffset.MinValue;
        var lastUsbScan = DateTimeOffset.MinValue;
        var initialUsbScan = true;

        while (!stoppingToken.IsCancellationRequested)
        {
            policy = policyStore.Get();
            try
            {
                var now = DateTimeOffset.UtcNow;
                if (now - lastUsbScan >= TimeSpan.FromSeconds(Math.Max(1, policy.Usb.PollSeconds)))
                {
                    await usbMonitor.TickAsync(initialUsbScan, stoppingToken);
                    initialUsbScan = false;
                    lastUsbScan = now;
                }

                await processMonitor.TickAsync(stoppingToken);
                await softwareMonitor.TickAsync(stoppingToken);
                await windowsAppControlAuditMonitor.TickAsync(stoppingToken);
                await permissionLifecycleMonitor.TickAsync(stoppingToken);
                await sessionAgentSupervisor.TickAsync(stoppingToken);

                if (policy.Runtime.Mode.Equals("Production", StringComparison.OrdinalIgnoreCase)
                    && policy.Runtime.PersistentProtection
                    && now - lastPolicyApply >= TimeSpan.FromSeconds(Math.Max(5, policy.Runtime.PolicyReapplySeconds)))
                {
                    await browserPolicyManager.ApplyMachinePoliciesAsync(stoppingToken);
                    lastPolicyApply = now;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "DLP protection loop failed; it will retry.");
            }

            var recorderPollMs = Math.Clamp(policy.Screen.RecorderPollMilliseconds, 100, 2000);
            await Task.Delay(TimeSpan.FromMilliseconds(recorderPollMs), stoppingToken);
        }

        await pipeTask;
    }
}
