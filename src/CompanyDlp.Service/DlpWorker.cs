namespace CompanyDlp.Service;

public sealed class DlpWorker(
    PolicyStore policyStore,
    PipeServer pipeServer,
    BrowserPolicyManager browserPolicyManager,
    UsbProtectionMonitor usbMonitor,
    ProcessProtectionMonitor processMonitor,
    SoftwareProtectionMonitor softwareMonitor,
    WindowsAppControlAuditMonitor windowsAppControlAuditMonitor,
    CliExecutionPolicyManager cliExecutionPolicyManager,
    CliExecutionAuditMonitor cliExecutionAuditMonitor,
    CliSensitiveCommandMonitor cliSensitiveCommandMonitor,
    PermissionLifecycleMonitor permissionLifecycleMonitor,
    SessionAgentSupervisor sessionAgentSupervisor,
    FileInventoryScanner fileInventoryScanner,
    ILogger<DlpWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var policy = policyStore.Reload();
        logger.LogInformation(
            "Company DLP started in {Mode} mode using {PolicyPath} (build {BuildIdentity})",
            policy.Runtime.Mode, policyStore.PolicyPath, BuildIdentity.Describe(policy));

        var pipeTask = pipeServer.RunAsync(stoppingToken);
        var lastPolicyApply = DateTimeOffset.MinValue;
        var lastUsbScan = DateTimeOffset.MinValue;
        var initialUsbScan = true;
        var lastFileInventoryScan = DateTimeOffset.MinValue;

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
                await cliExecutionAuditMonitor.TickAsync(stoppingToken);
                await cliSensitiveCommandMonitor.TickAsync(stoppingToken);
                await permissionLifecycleMonitor.TickAsync(stoppingToken);
                await sessionAgentSupervisor.TickAsync(stoppingToken);

                if (now - lastFileInventoryScan >= TimeSpan.FromSeconds(Math.Max(5, policy.FileClassification.ScanIntervalSeconds)))
                {
                    await fileInventoryScanner.TickAsync(policy, stoppingToken);
                    lastFileInventoryScan = now;
                }

                if (policy.Runtime.Mode.Equals("Production", StringComparison.OrdinalIgnoreCase)
                    && policy.Runtime.PersistentProtection
                    && now - lastPolicyApply >= TimeSpan.FromSeconds(Math.Max(5, policy.Runtime.PolicyReapplySeconds)))
                {
                    await browserPolicyManager.ApplyMachinePoliciesAsync(stoppingToken);

                    // CLI execution blocking is temporarily disabled - business decision, not a
                    // technical issue. The feature (CliExecutionPolicyManager.cs), its tests, and the
                    // `cli` policy config section are fully intact and verified (160/160 tests passing
                    // as of 2026-08-15). Re-enable by uncommenting this call. Do not delete or modify
                    // CliExecutionPolicyManager.cs, its tests, or the cli config schema while
                    // investigating this.
                    // await cliExecutionPolicyManager.ApplyMachinePoliciesAsync(stoppingToken);

                    // SelfHealLegacyAppLockerExeEnforcement must keep running every cycle regardless of
                    // the pause above - it cleans up dangerous leftover AppLocker state from old agent
                    // builds and is independent of whether CLI blocking itself is active. Extracted to
                    // CliExecutionPolicyManager.RunSelfHealOnly() so it isn't disabled as a side effect
                    // of commenting out ApplyMachinePoliciesAsync. Do not remove this call.
                    cliExecutionPolicyManager.RunSelfHealOnly();

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
