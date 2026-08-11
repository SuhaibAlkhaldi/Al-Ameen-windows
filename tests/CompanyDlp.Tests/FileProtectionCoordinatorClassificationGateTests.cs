using CompanyDlp.Contracts;
using CompanyDlp.Core;
using CompanyDlp.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CompanyDlp.Tests;

// Live, end-to-end verification of the classification-tier-gated file.decrypt feature: real AES-GCM
// encryption/decryption of a real file on disk via FileProtectionEngine, real fileId->hash persistence
// via EncryptedFileHashStore, and real PermissionEvaluator decisions - driven entirely through
// FileProtectionCoordinator.ExecuteAsync, the same entry point the pipe server uses. The only thing
// stubbed out is the AI classification network call itself (the policy's default FileClassification
// provider is BlockAll, and classification results below are seeded directly into
// FileClassificationCache exactly like FileInventoryScanner would after a real AI response) - there is
// no live AI endpoint to reach from a test run, and the point of this suite is the gating logic around
// that result, not the AI call itself.
public sealed class FileProtectionCoordinatorClassificationGateTests : IDisposable
{
    private readonly string _workDirectory;
    private readonly PolicyStore _policyStore;
    private readonly AgentIdentityProvider _identityProvider;
    private readonly FileClassificationCache _classificationCache;
    private readonly EncryptedFileHashStore _hashStore;
    private readonly LocalEntityExtractor _entityExtractor;
    private readonly FileProtectionCoordinator _coordinator;
    private readonly ClientContext _context = new()
    {
        UserSid = "S-1-5-21-1000",
        Username = "TEST\\employee",
        MachineName = "TEST-PC",
        WindowsSessionId = 2,
        ClientName = "test"
    };

    public FileProtectionCoordinatorClassificationGateTests()
    {
        _workDirectory = Path.Combine(Path.GetTempPath(), "CompanyDlpTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDirectory);

        var dataProtector = new MachineDataProtector();
        _policyStore = new PolicyStore(dataProtector, NullLogger<PolicyStore>.Instance);
        // Default policy already has FileEncrypt/FileDecrypt DefaultPermissions = true and
        // KeyProvider = LocalMachineDpapi (no network/backend dependency for key wrap/unwrap).
        _identityProvider = new AgentIdentityProvider(_policyStore, NullLogger<AgentIdentityProvider>.Instance);
        _classificationCache = new FileClassificationCache(_policyStore, NullLogger<FileClassificationCache>.Instance);
        _hashStore = new EncryptedFileHashStore(_policyStore, NullLogger<EncryptedFileHashStore>.Instance);

        var permissionEvaluator = new PermissionEvaluator(_classificationCache);
        var keyProtector = new FileKeyProtector(_policyStore, _identityProvider, dataProtector, backendApiClient: null!);
        var engine = new FileProtectionEngine(_policyStore, keyProtector);

        var blockAllProvider = new BlockAllFileClassificationProvider();
        // Never actually invoked below: the default policy's FileClassification.Provider is BlockAll
        // (see FileClassificationPolicy), so classification-service routing always picks
        // blockAllProvider - this instance only needs to exist to satisfy the constructor. Points at
        // the real deployed model assets (same relative layout Program.cs resolves at runtime) rather
        // than stubbing LocalEntityExtractor, since it has no interface seam to fake through.
        var aiModelDirectory = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "CompanyDlp.Service", "AiModel");
        _entityExtractor = new LocalEntityExtractor(
            Path.Combine(aiModelDirectory, "gliner_model.onnx"),
            Path.Combine(aiModelDirectory, "spm.model"));
        var dictionaryRuleStore = new DictionaryRuleStore(_policyStore, NullLogger<DictionaryRuleStore>.Instance);
        var aiApiProvider = new LocalAiFileClassificationProvider(_entityExtractor, dictionaryRuleStore);
        var classificationService = new FileClassificationService(
            _policyStore, _identityProvider, blockAllProvider, aiApiProvider, NullLogger<FileClassificationService>.Instance);

        var eventFactory = new SecurityEventFactory(_policyStore, _identityProvider);
        var outbox = new AuditOutbox(_policyStore, dataProtector, NullLogger<AuditOutbox>.Instance);
        var auditLogger = new AuditLogger(_policyStore, eventFactory, outbox, NullLogger<AuditLogger>.Instance);

        _coordinator = new FileProtectionCoordinator(
            _policyStore,
            _identityProvider,
            permissionEvaluator,
            engine,
            auditLogger,
            _classificationCache,
            classificationService,
            _hashStore,
            NullLogger<FileProtectionCoordinator>.Instance);
    }

    public void Dispose()
    {
        _entityExtractor.Dispose();
        try { Directory.Delete(_workDirectory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string WritePlaintextFile(string name, string content)
    {
        var path = Path.Combine(_workDirectory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private async Task<string> EncryptAsync(string plaintextPath)
    {
        var response = await _coordinator.ExecuteAsync(
            new FileProtectionRequest { Action = "encrypt", FilePath = plaintextPath },
            _context,
            CancellationToken.None);

        Assert.True(response.Success, $"Encrypt failed: {response.ErrorCode} {response.Message}");
        Assert.True(File.Exists(response.OutputPath));
        Assert.False(File.Exists(plaintextPath)); // plaintext deleted per policy default
        return response.OutputPath;
    }

    private void SeedClassification(string plaintextPath, string tier)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(plaintextPath))).ToLowerInvariant();
        _classificationCache.Set(new CachedFileClassification(hash, tier, "AiClassified", DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task PublicFile_DecryptsWithNoGrantAtAll()
    {
        var plaintext = WritePlaintextFile("public.txt", "public content");
        SeedClassification(plaintext, ClassificationTiers.Public);

        var encryptedPath = await EncryptAsync(plaintext);

        var response = await _coordinator.ExecuteAsync(
            new FileProtectionRequest { Action = "decrypt", FilePath = encryptedPath },
            _context,
            CancellationToken.None);

        Assert.True(response.Success, $"Decrypt should have been allowed for a Public file: {response.ErrorCode} {response.Message}");
        Assert.Equal("public content", await File.ReadAllTextAsync(response.OutputPath));
    }

    [Fact]
    public async Task SecretFile_IsBlockedByDefault_ThenGrantedThenRevoked()
    {
        var plaintext = WritePlaintextFile("secret.txt", "secret content");
        SeedClassification(plaintext, ClassificationTiers.Secret);
        var encryptedPath = await EncryptAsync(plaintext);

        // 1. No grant -> blocked.
        var blocked = await _coordinator.ExecuteAsync(
            new FileProtectionRequest { Action = "decrypt", FilePath = encryptedPath },
            _context,
            CancellationToken.None);
        Assert.False(blocked.Success);
        Assert.Equal("PermissionDenied", blocked.ErrorCode);
        Assert.True(File.Exists(encryptedPath)); // still encrypted, nothing leaked

        // 2. Grant file.decrypt scoped to tier Secret -> now allowed.
        var grant = new PermissionGrant
        {
            ActionKey = ActionKeys.FileDecrypt,
            Allowed = true,
            SubjectType = PermissionSubjectTypes.UserSid,
            SubjectId = _context.UserSid,
            Source = PermissionSources.TemporaryGrant,
            StartsAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(10),
            ClassificationTier = ClassificationTiers.Secret,
            Priority = 500
        };
        _policyStore.Get().Permissions.Grants.Add(grant);

        var allowed = await _coordinator.ExecuteAsync(
            new FileProtectionRequest { Action = "decrypt", FilePath = encryptedPath },
            _context,
            CancellationToken.None);
        Assert.True(allowed.Success, $"Decrypt should have been allowed once granted: {allowed.ErrorCode} {allowed.Message}");
        Assert.Equal("secret content", await File.ReadAllTextAsync(allowed.OutputPath));

        // 3. Revoke -> re-encrypt the recovered plaintext and confirm decrypt is blocked again.
        grant.RevokedAtUtc = DateTimeOffset.UtcNow;
        var plaintextAgain = WritePlaintextFile("secret2.txt", "secret content 2");
        SeedClassification(plaintextAgain, ClassificationTiers.Secret);
        var encryptedAgain = await EncryptAsync(plaintextAgain);

        var blockedAgain = await _coordinator.ExecuteAsync(
            new FileProtectionRequest { Action = "decrypt", FilePath = encryptedAgain },
            _context,
            CancellationToken.None);
        Assert.False(blockedAgain.Success);
        Assert.Equal("PermissionDenied", blockedAgain.ErrorCode);
    }

    [Fact]
    public async Task SecretGrant_DoesNotUnlockAVerySecretFile_ButAVerySecretGrantDoes()
    {
        var secretPlaintext = WritePlaintextFile("secret.txt", "secret content");
        SeedClassification(secretPlaintext, ClassificationTiers.Secret);
        var secretEncrypted = await EncryptAsync(secretPlaintext);

        var verySecretPlaintext = WritePlaintextFile("verysecret.txt", "very secret content");
        SeedClassification(verySecretPlaintext, ClassificationTiers.VerySecret);
        var verySecretEncrypted = await EncryptAsync(verySecretPlaintext);

        var grant = new PermissionGrant
        {
            ActionKey = ActionKeys.FileDecrypt,
            Allowed = true,
            SubjectType = PermissionSubjectTypes.UserSid,
            SubjectId = _context.UserSid,
            Source = PermissionSources.TemporaryGrant,
            StartsAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(10),
            ClassificationTier = ClassificationTiers.Secret,
            Priority = 500
        };
        _policyStore.Get().Permissions.Grants.Add(grant);

        var secretResult = await _coordinator.ExecuteAsync(
            new FileProtectionRequest { Action = "decrypt", FilePath = secretEncrypted }, _context, CancellationToken.None);
        var verySecretResult = await _coordinator.ExecuteAsync(
            new FileProtectionRequest { Action = "decrypt", FilePath = verySecretEncrypted }, _context, CancellationToken.None);

        Assert.True(secretResult.Success);
        Assert.False(verySecretResult.Success);

        // Now switch the grant's tier to VerySecret - the same file that was previously denied
        // must now decrypt (hierarchy holds: a broader/more-sensitive tier grant covers narrower ones).
        grant.ClassificationTier = ClassificationTiers.VerySecret;

        var verySecretResultAfterUpgrade = await _coordinator.ExecuteAsync(
            new FileProtectionRequest { Action = "decrypt", FilePath = verySecretEncrypted }, _context, CancellationToken.None);
        Assert.True(verySecretResultAfterUpgrade.Success);
        Assert.Equal("very secret content", await File.ReadAllTextAsync(verySecretResultAfterUpgrade.OutputPath));
    }

    [Fact]
    public async Task UngatedGrant_UnlocksEveryTierIncludingVerySecret()
    {
        var plaintext = WritePlaintextFile("verysecret.txt", "top secret content");
        SeedClassification(plaintext, ClassificationTiers.VerySecret);
        var encryptedPath = await EncryptAsync(plaintext);

        _policyStore.Get().Permissions.Grants.Add(new PermissionGrant
        {
            ActionKey = ActionKeys.FileDecrypt,
            Allowed = true,
            SubjectType = PermissionSubjectTypes.UserSid,
            SubjectId = _context.UserSid,
            Source = PermissionSources.TemporaryGrant,
            StartsAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(10),
            Priority = 500
            // No FileHash, no ClassificationTier - plain, ungated grant.
        });

        var result = await _coordinator.ExecuteAsync(
            new FileProtectionRequest { Action = "decrypt", FilePath = encryptedPath }, _context, CancellationToken.None);

        Assert.True(result.Success, $"An ungated grant must still unlock every tier: {result.ErrorCode} {result.Message}");
        Assert.Equal("top secret content", await File.ReadAllTextAsync(result.OutputPath));
    }

    [Fact]
    public async Task EncryptRemainsUnaffected_ByTheDecryptClassificationGate()
    {
        // file.encrypt has no tier gating at all - confirm a brand-new, never-classified file still
        // encrypts successfully (encrypt only triggers best-effort classification, never blocks on it).
        var plaintext = WritePlaintextFile("unclassified.txt", "unclassified content");

        var response = await _coordinator.ExecuteAsync(
            new FileProtectionRequest { Action = "encrypt", FilePath = plaintext }, _context, CancellationToken.None);

        Assert.True(response.Success, $"Encrypt must remain unaffected by decrypt's classification gate: {response.ErrorCode} {response.Message}");
    }

    [Fact]
    public async Task LegacyEncryptedFile_WithNoHashStoreEntry_IsFailClosed_NotASpecialCase()
    {
        // Simulates a .dlpenc file encrypted before this feature existed (or whose hash-store entry
        // was lost) - old files are not meant to be a loophole, so EncryptedFileHashStore falling back
        // to its deterministic per-fileId sentinel must gate this exactly like any other unclassified
        // file: blocked with no grant.
        var plaintext = WritePlaintextFile("legacy.txt", "legacy content");
        var encryptedPath = await EncryptAsync(plaintext);

        // Simulate "lost"/missing classification linkage - the coordinator's PeekFileIdAsync will
        // still find the fileId in the header, but the store will have no entry for it.
        var storeField = typeof(EncryptedFileHashStore).GetField("_entries", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        storeField.SetValue(_hashStore, new Dictionary<Guid, EncryptedFileHashEntry>());

        var blocked = await _coordinator.ExecuteAsync(
            new FileProtectionRequest { Action = "decrypt", FilePath = encryptedPath }, _context, CancellationToken.None);
        Assert.False(blocked.Success, "An old/legacy file must not get a free pass just because it predates this feature.");
        Assert.Equal("PermissionDenied", blocked.ErrorCode);

        // An ungated grant still unlocks it, exactly like any other unclassified/VerySecret-ranked file.
        _policyStore.Get().Permissions.Grants.Add(new PermissionGrant
        {
            ActionKey = ActionKeys.FileDecrypt,
            Allowed = true,
            SubjectType = PermissionSubjectTypes.UserSid,
            SubjectId = _context.UserSid,
            Source = PermissionSources.TemporaryGrant,
            StartsAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(10),
            Priority = 500
        });

        var allowed = await _coordinator.ExecuteAsync(
            new FileProtectionRequest { Action = "decrypt", FilePath = encryptedPath }, _context, CancellationToken.None);
        Assert.True(allowed.Success, $"An ungated grant must still unlock a legacy file: {allowed.ErrorCode} {allowed.Message}");
        Assert.Equal("legacy content", await File.ReadAllTextAsync(allowed.OutputPath));
    }
}
