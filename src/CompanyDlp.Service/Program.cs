using CompanyDlp.Contracts;
using CompanyDlp.Core;
using CompanyDlp.Service;

var enrollmentMode = args.Any(value => value.Equals("--enroll", StringComparison.OrdinalIgnoreCase));
var hostArguments = args.Where(value => !value.Equals("--enroll", StringComparison.OrdinalIgnoreCase)).ToArray();
var builder = Host.CreateApplicationBuilder(hostArguments);

builder.Services.AddWindowsService(options => options.ServiceName = "Company DLP Service");
builder.Services.AddHttpClient("CompanyDlp.Backend");

builder.Services.AddSingleton<MachineDataProtector>();
builder.Services.AddSingleton<PolicyStore>();
builder.Services.AddSingleton<AgentIdentityProvider>();
builder.Services.AddSingleton<AgentCredentialStore>();
builder.Services.AddSingleton<BackendRequestAuthenticator>();
builder.Services.AddSingleton<TrustedClock>();
builder.Services.AddSingleton<ITrustedClock>(provider => provider.GetRequiredService<TrustedClock>());
builder.Services.AddSingleton<PermissionEvaluator>();
builder.Services.AddSingleton<PermissionLifecycleMonitor>();
builder.Services.AddSingleton<SessionAgentSupervisor>();
builder.Services.AddSingleton<InteractiveUserContextProvider>();
builder.Services.AddSingleton<EffectivePolicyBuilder>();
builder.Services.AddSingleton<ContentNormalizer>();
builder.Services.AddSingleton<FragmentSessionTracker>();
builder.Services.AddSingleton<ContentClassifier>();
builder.Services.AddSingleton<BlockAllFileClassificationProvider>();
builder.Services.AddSingleton<AiApiFileClassificationProvider>();
builder.Services.AddSingleton<FileClassificationService>();
builder.Services.AddSingleton<SecurityEventFactory>();
builder.Services.AddSingleton<AuditOutbox>();
builder.Services.AddSingleton<AuditLogger>();
builder.Services.AddSingleton<BackendApiClient>();
builder.Services.AddSingleton<PolicySnapshotValidator>();
builder.Services.AddSingleton<IFileKeyProtector, FileKeyProtector>();
builder.Services.AddSingleton<FileProtectionEngine>();
builder.Services.AddSingleton<FileProtectionCoordinator>();
builder.Services.AddSingleton<NotificationStore>();
builder.Services.AddSingleton<BrowserPolicyManager>();
builder.Services.AddSingleton<RuntimeOverrideStore>();
builder.Services.AddSingleton<UsbDeviceInventory>();
builder.Services.AddSingleton<UsbBaselineStore>();
builder.Services.AddSingleton<UsbDeviceController>();
builder.Services.AddSingleton<UsbProtectionMonitor>();
builder.Services.AddSingleton<ProcessProtectionMonitor>();
builder.Services.AddSingleton<SoftwareProtectionMonitor>();
builder.Services.AddSingleton<WindowsAppControlAuditMonitor>();
builder.Services.AddSingleton<PipeServer>();

builder.Services.AddHostedService<DlpWorker>();
builder.Services.AddHostedService<AuditSyncWorker>();
builder.Services.AddHostedService<PolicySyncWorker>();
builder.Services.AddHostedService<HeartbeatWorker>();

var host = builder.Build();
var policyStore = host.Services.GetRequiredService<PolicyStore>();
var policy = policyStore.Reload();
ValidateProductionReadiness(policy, enrollmentMode, host.Services.GetRequiredService<AgentCredentialStore>());

if (enrollmentMode)
{
    var enrollmentCode = Environment.GetEnvironmentVariable("COMPANY_DLP_ENROLLMENT_CODE");
    if (string.IsNullOrWhiteSpace(enrollmentCode))
        throw new InvalidOperationException(
            "COMPANY_DLP_ENROLLMENT_CODE must be set for --enroll. The code is intentionally not accepted as a command-line argument.");

    var identity = host.Services.GetRequiredService<AgentIdentityProvider>().Get();
    if (policy.Backend.TenantId == Guid.Empty)
        throw new InvalidOperationException("A non-empty backend tenantId is required before agent enrollment.");

    var result = await host.Services.GetRequiredService<BackendApiClient>().EnrollAsync(
        new AgentEnrollmentRequest
        {
            TenantId = identity.TenantId,
            DeviceId = identity.DeviceId,
            MachineName = identity.MachineName,
            AgentVersion = identity.AgentVersion,
            EnrollmentCode = enrollmentCode
        },
        CancellationToken.None);

    Console.WriteLine($"Company DLP device {identity.DeviceId:D} enrolled. Credential expires at {result.ExpiresAtUtc:O}.");
    return;
}

await host.RunAsync();

static void ValidateProductionReadiness(
    DlpPolicy policy,
    bool enrollmentMode,
    AgentCredentialStore credentialStore)
{
    var failures = ProductionReadinessValidator.Validate(policy).ToList();
    if (policy.Runtime.Mode.Equals("Production", StringComparison.OrdinalIgnoreCase)
        && !enrollmentMode
        && policy.Backend.Enabled
        && policy.Backend.AuthenticationMode.Equals(BackendAuthenticationModes.DeviceBearerToken, StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrWhiteSpace(credentialStore.Load(policy.Backend.CredentialName)))
    {
        failures.Add("Production agent is not enrolled. Run --enroll with COMPANY_DLP_ENROLLMENT_CODE before starting the service.");
    }

    if (failures.Count == 0) return;

    var message = "Company DLP production readiness validation failed:"
        + Environment.NewLine
        + string.Join(Environment.NewLine, failures.Select(item => "- " + item));
    throw new InvalidOperationException(message);
}
