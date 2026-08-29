using CompanyDlp.Contracts;
using CompanyDlp.Core;
using CompanyDlp.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CompanyDlp.Tests;

// Drives the real FileClassificationStatusStore -> FileClassificationCache -> FileClassificationStatusResolver
// chain (no mocks) - this is exactly what backs the getFileClassificationStatus pipe message
// CompanyDlp.ShellExtension's DLP Properties tab depends on (see PipeServer.cs / StatusPipeClient.cs).
// FileClassificationStatusStore and FileClassificationCache both resolve their storage root from
// Environment.GetFolderPath(SpecialFolder.LocalApplicationData) with no override seam (unlike
// PolicyStore's COMPANY_DLP_POLICY_PATH) - confirmed the LOCALAPPDATA env var does NOT redirect that
// call on .NET 8/Windows, so this follows the same real-file backup/restore convention already used
// by TrustedClockTests.cs rather than inventing a new mechanism.
public sealed class FileClassificationStatusResolverTests : IDisposable
{
    private readonly string _policyDirectory = Path.Combine(Path.GetTempPath(), "CompanyDlpStatusResolverTests", Guid.NewGuid().ToString("N"));
    private readonly string? _oldPolicyPath = Environment.GetEnvironmentVariable("COMPANY_DLP_POLICY_PATH");

    private readonly string _statusStorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CompanyDlp", "file-classification-status.json");
    private readonly string _cachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CompanyDlp", "file-classification-cache.json");

    private readonly byte[]? _statusStoreBackup;
    private readonly byte[]? _cacheBackup;

    public FileClassificationStatusResolverTests()
    {
        Directory.CreateDirectory(_policyDirectory);
        Environment.SetEnvironmentVariable("COMPANY_DLP_POLICY_PATH", Path.Combine(_policyDirectory, "policy.json"));

        _statusStoreBackup = File.Exists(_statusStorePath) ? File.ReadAllBytes(_statusStorePath) : null;
        _cacheBackup = File.Exists(_cachePath) ? File.ReadAllBytes(_cachePath) : null;
    }

    private static (FileClassificationStatusResolver Resolver, FileClassificationStatusStore StatusStore, FileClassificationCache Cache) CreateResolver()
    {
        var policyStore = new PolicyStore(new MachineDataProtector(), NullLogger<PolicyStore>.Instance);
        var statusStore = new FileClassificationStatusStore(policyStore, NullLogger<FileClassificationStatusStore>.Instance);
        var cache = new FileClassificationCache(policyStore, NullLogger<FileClassificationCache>.Instance);

        // IsScanning() (the only scanner member the resolver calls) only reads an in-memory
        // ConcurrentDictionary never touched by these constructor args, so passing null for the
        // dependencies unused by that path is safe - same reasoning PipeServerTests already
        // documents for its own null dependencies. Includes PermissionEvaluator/AgentIdentityProvider/
        // WatermarkEscrowStore (added for ActionKeys.FileWatermarkDisable's grant re-check) for the
        // same reason - IsScanning() never touches them either.
        var scanner = new FileInventoryScanner(
            classificationService: null!,
            cache,
            statusStore,
            dictionaryRuleStore: null!,
            interactiveUserContextProvider: null!,
            permissionEvaluator: null!,
            identityProvider: null!,
            escrowStore: null!,
            NullLogger<FileInventoryScanner>.Instance);

        // engine/encryptedFileHashStore are only touched by ResolveEncryptedAsync's .dlpenc branch -
        // every test below exercises the ordinary (non-.dlpenc) path only, so null is safe here too.
        return (new FileClassificationStatusResolver(statusStore, cache, scanner, engine: null!, encryptedFileHashStore: null!), statusStore, cache);
    }

    [Fact]
    public async Task Resolve_PathNeverSeen_ReturnsNotScanned()
    {
        var (resolver, _, _) = CreateResolver();
        var path = Path.Combine(_policyDirectory, "never-seen.txt");

        var result = await resolver.ResolveAsync(path);

        Assert.Equal(FileClassificationStatuses.NotScanned, result.Status);
        Assert.Equal("Unclassified", result.Classification);
        Assert.Null(result.LastScannedAtUtc);
    }

    [Fact]
    public async Task Resolve_UpToDateWithCachedHash_ReturnsRealClassification()
    {
        var (resolver, statusStore, cache) = CreateResolver();
        var path = Path.Combine(_policyDirectory, "secret.docx");
        var normalized = FileClassificationStatusStore.NormalizePath(path);
        var scannedAt = DateTimeOffset.UtcNow;

        cache.Set(new CachedFileClassification("hash-abc", "Very_Secret", "RealAiVerdict", scannedAt, RulesVersion: 1));
        statusStore.Set(new FileClassificationStatusEntry(normalized, FileClassificationStatuses.UpToDate, "hash-abc", scannedAt, "RealAiVerdict", scannedAt));

        var result = await resolver.ResolveAsync(path);

        Assert.Equal(FileClassificationStatuses.UpToDate, result.Status);
        Assert.Equal("Very_Secret", result.Classification);
        Assert.Equal(scannedAt, result.LastScannedAtUtc);
    }

    [Fact]
    public async Task Resolve_FailedStatus_NeverShowsAStaleClassification()
    {
        // Guards the resolver's core safety rule (see FileClassificationStatusResolver.cs): even
        // when a cache entry still exists for the last-known hash, a non-UpToDate status must
        // always display "Unclassified" instead of risking a verdict the file may no longer deserve.
        var (resolver, statusStore, cache) = CreateResolver();
        var path = Path.Combine(_policyDirectory, "flaky.pdf");
        var normalized = FileClassificationStatusStore.NormalizePath(path);
        var scannedAt = DateTimeOffset.UtcNow;

        cache.Set(new CachedFileClassification("hash-def", "Public", "RealAiVerdict", scannedAt, RulesVersion: 1));
        statusStore.Set(new FileClassificationStatusEntry(normalized, FileClassificationStatuses.Failed, "hash-def", scannedAt, "AiApiTransientError", scannedAt));

        var result = await resolver.ResolveAsync(path);

        Assert.Equal(FileClassificationStatuses.Failed, result.Status);
        Assert.Equal("Unclassified", result.Classification);
    }

    [Fact]
    public async Task Resolve_UpToDateButCacheEntryMissing_FallsBackToUnclassified()
    {
        var (resolver, statusStore, _) = CreateResolver();
        var path = Path.Combine(_policyDirectory, "orphaned-hash.txt");
        var normalized = FileClassificationStatusStore.NormalizePath(path);
        var scannedAt = DateTimeOffset.UtcNow;

        statusStore.Set(new FileClassificationStatusEntry(normalized, FileClassificationStatuses.UpToDate, "hash-not-in-cache", scannedAt, "RealAiVerdict", scannedAt));

        var result = await resolver.ResolveAsync(path);

        Assert.Equal(FileClassificationStatuses.UpToDate, result.Status);
        Assert.Equal("Unclassified", result.Classification);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("COMPANY_DLP_POLICY_PATH", _oldPolicyPath);
        try { Directory.Delete(_policyDirectory, true); } catch { }

        RestoreOrDelete(_statusStorePath, _statusStoreBackup);
        RestoreOrDelete(_cachePath, _cacheBackup);
    }

    private static void RestoreOrDelete(string path, byte[]? backup)
    {
        try
        {
            if (backup is null) File.Delete(path);
            else File.WriteAllBytes(path, backup);
        }
        catch
        {
            // Best-effort cleanup only, mirroring TrustedClockTests.cs's Dispose - a failure here
            // must never fail the test that already ran.
        }
    }
}
