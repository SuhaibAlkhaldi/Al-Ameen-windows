using System.Text.Json;
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
builder.Services.AddSingleton(provider =>
{
    var baseDirectory = AppContext.BaseDirectory;
    var tessDataPath = provider.GetRequiredService<IConfiguration>()["LocalAi:TessDataPath"]
        ?? Path.Combine(baseDirectory, "TessData");
    var ocrLanguages = provider.GetRequiredService<IConfiguration>()["LocalAi:OcrLanguages"] ?? "eng+ara";
    return new ImageOcrExtractor(tessDataPath, ocrLanguages);
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
builder.Services.AddSingleton<ExtensionHealthChecker>();
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

// Its own dedicated BackgroundService, not ticked from DlpWorker's shared loop - see
// PrintProtectionMonitor's class comment for why (a print job's cancellable window is too brief to
// share a poll cadence meant for screen-recording detection).
builder.Services.AddHostedService<PrintProtectionMonitor>();
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
DocumentTextExtractor.ConfigureImageOcr(host.Services.GetRequiredService<ImageOcrExtractor>());

if (enrollmentMode)
{
    var enrollmentCode = Environment.GetEnvironmentVariable("COMPANY_DLP_ENROLLMENT_CODE");
    if (string.IsNullOrWhiteSpace(enrollmentCode))
        throw new InvalidOperationException(
            "COMPANY_DLP_ENROLLMENT_CODE must be set for --enroll. The code is intentionally not accepted as a command-line argument.");

    var identity = host.Services.GetRequiredService<AgentIdentityProvider>().Get();
    if (policy.Backend.TenantId == Guid.Empty)
        throw new InvalidOperationException("A non-empty backend tenantId is required before agent enrollment.");

    // Wrapped in try/catch instead of letting a failed enrollment bubble up as an unhandled
    // exception - confirmed live 2026-08-25: a second device's install hit "Cannot start service
    // CompanyDlp" with no visible reason, because (a) this block previously let .NET's default
    // unhandled-exception handler print a full stack trace that scrolled out of the installer
    // console before anyone could read it, and (b) deploy-agent-portable.ps1 never checked whether
    // --enroll actually succeeded before moving on to Start-Service, so the *real* failure (a bad or
    // already-used enrollment code) was masked by a second, generic, unrelated-looking SCM error.
    // BackendApiClient.EnrollAsync already extracts the backend's bilingual messageEn/messageAr into
    // the exception text for exactly this reason - ExtractEnrollmentFailureReason below pulls just
    // that one line back out so the operator sees e.g. "Enrollment code usage limit has been
    // reached." instead of a wall of CLR stack frames.
    try
    {
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
    catch (Exception ex)
    {
        Console.Error.WriteLine("Al-Ameen enrollment failed: " + ExtractEnrollmentFailureReason(ex));
        Environment.Exit(2);
        return;
    }
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

// BackendApiClient.EnrollAsync formats failures as
// "Agent enrollment failed with 400 (Bad Request). Response: {"success":false,"messageEn":"...",...}"
// - the JSON after "Response: " is the backend's raw ApiResponse body. Pull just messageEn back out
// so the operator sees one clean sentence ("Invalid or expired enrollment code." /
// "Enrollment code usage limit has been reached.") instead of the whole wrapped exception text. Any
// exception shape this doesn't recognize (network failure, timeout, malformed body from a proxy)
// falls back to ex.Message as-is rather than hiding it.
static string ExtractEnrollmentFailureReason(Exception ex)
{
    const string marker = "Response: ";
    var index = ex.Message.IndexOf(marker, StringComparison.Ordinal);
    if (index < 0) return ex.Message;

    var json = ex.Message[(index + marker.Length)..].Trim();
    try
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("messageEn", out var messageEn)
            && messageEn.GetString() is { Length: > 0 } text)
        {
            return text;
        }
    }
    catch (JsonException)
    {
        // Not parseable JSON - fall through to the raw exception message below.
    }

    return ex.Message;
}
