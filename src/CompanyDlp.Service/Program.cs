using CompanyDlp.Contracts;
using CompanyDlp.Core;
using CompanyDlp.Service;

var enrollmentMode = args.Any(value => value.Equals("--enroll", StringComparison.OrdinalIgnoreCase));
var versionMode = args.Any(value => value.Equals("--version", StringComparison.OrdinalIgnoreCase));
var hostArguments = args.Where(value =>
    !value.Equals("--enroll", StringComparison.OrdinalIgnoreCase) &&
    !value.Equals("--version", StringComparison.OrdinalIgnoreCase)).ToArray();
var builder = Host.CreateApplicationBuilder(hostArguments);

builder.Services.AddWindowsService(options => options.ServiceName = "Al-Ameen Service");
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

// LocalEntityExtractor's constructor loads the ONNX model and SentencePiece tokenizer files
// unconditionally (File.OpenRead + new InferenceSession, no try/catch) - the model files are ~1.1GB
// and deliberately excluded from git (see AiModel/.gitignore), so a machine that hasn't had them
// deployed yet must not fail the whole service's startup over it. This singleton is registered as
// nullable/optional: if either file is missing, no LocalEntityExtractor is constructed at all (just a
// warning explaining exactly what's missing and where it was expected), and
// LocalAiFileClassificationProvider falls back to a block-all decision instead of trying to use it -
// see that class's ClassifyAsync.
builder.Services.AddSingleton<LocalEntityExtractor>(provider =>
{
    var baseDirectory = AppContext.BaseDirectory;
    var onnxModelPath = provider.GetRequiredService<IConfiguration>()["LocalAi:OnnxModelPath"]
        ?? Path.Combine(baseDirectory, "AiModel", "gliner_model.onnx");
    var tokenizerModelPath = provider.GetRequiredService<IConfiguration>()["LocalAi:TokenizerModelPath"]
        ?? Path.Combine(baseDirectory, "AiModel", "spm.model");

    var missingPaths = new List<string>();
    if (!File.Exists(onnxModelPath)) missingPaths.Add(onnxModelPath);
    if (!File.Exists(tokenizerModelPath)) missingPaths.Add(tokenizerModelPath);

    if (missingPaths.Count > 0)
    {
        provider.GetRequiredService<ILogger<Program>>().LogWarning(
            "Local AI file classification is unavailable - the following model file(s) are missing: {MissingModelPaths}. " +
            "File classification will fail closed (block-all) until these are deployed to this device.",
            string.Join(", ", missingPaths));
        // The DI container itself doesn't care about this factory's declared nullability - only the
        // runtime value it returns - so registering as non-nullable LocalEntityExtractor (rather than
        // LocalEntityExtractor?, which trips the AddSingleton<T> `where T : class` constraint) and
        // null-forgiving this specific return is the clean way to make GetService<LocalEntityExtractor>()
        // still yield null here. LocalAiFileClassificationProvider's constructor parameter is the one
        // actually annotated nullable, which is what makes that null flow through correctly.
        return null!;
    }

    return new LocalEntityExtractor(onnxModelPath, tokenizerModelPath);
});
builder.Services.AddSingleton<LocalAiFileClassificationProvider>();
builder.Services.AddSingleton<FileClassificationService>();
builder.Services.AddSingleton<FileClassificationCache>();
builder.Services.AddSingleton<EncryptedFileHashStore>();
builder.Services.AddSingleton<FileClassificationStatusStore>();
builder.Services.AddSingleton<FileInventoryScanner>();
builder.Services.AddSingleton<FileClassificationStatusResolver>();
builder.Services.AddSingleton<DictionaryRuleStore>();
builder.Services.AddSingleton<SecurityEventFactory>();
builder.Services.AddSingleton<AuditOutbox>();
builder.Services.AddSingleton<AuditLogger>();
builder.Services.AddSingleton<BackendApiClient>();
builder.Services.AddSingleton<PolicySnapshotValidator>();
builder.Services.AddSingleton<PolicyRefreshSignal>();
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
builder.Services.AddSingleton<CliExecutionPolicyManager>();
builder.Services.AddSingleton<CliExecutionAuditMonitor>();
builder.Services.AddSingleton<CliSensitiveCommandMonitor>();
builder.Services.AddSingleton<PipeServer>();

builder.Services.AddHostedService<DlpWorker>();
builder.Services.AddHostedService<AuditSyncWorker>();
builder.Services.AddHostedService<PolicySyncWorker>();
builder.Services.AddHostedService<DictionaryRuleSyncWorker>();
builder.Services.AddHostedService<HeartbeatWorker>();

var host = builder.Build();
var policyStore = host.Services.GetRequiredService<PolicyStore>();
var policy = policyStore.Reload();

// Must work even on a device that isn't enrolled yet or otherwise fails ValidateProductionReadiness
// below - "which build is this" is exactly the question someone needs answered right after copying
// binaries down, before enrollment has even happened. Reading policy.json (already done above) is the
// only thing --version needs.
if (versionMode)
{
    Console.WriteLine(BuildIdentity.Describe(policy));
    return;
}

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

    Console.WriteLine($"Al-Ameen device {identity.DeviceId:D} enrolled. Credential expires at {result.ExpiresAtUtc:O}.");
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

    var message = "Al-Ameen production readiness validation failed:"
        + Environment.NewLine
        + string.Join(Environment.NewLine, failures.Select(item => "- " + item));
    throw new InvalidOperationException(message);
}
