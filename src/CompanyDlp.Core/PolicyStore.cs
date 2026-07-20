using System.Security.Cryptography;
using System.Text.Json;
using CompanyDlp.Contracts;
using Microsoft.Extensions.Logging;

namespace CompanyDlp.Core;

public sealed class PolicyStore(
    MachineDataProtector dataProtector,
    ILogger<PolicyStore> logger)
{
    private readonly object _sync = new();
    private DlpPolicy _policy = CreateDefault();
    private long _currentRemoteVersion;
    private Guid? _currentRemotePolicyId;

    public string PolicyPath { get; } = ResolvePolicyPath();
    public long CurrentRemoteVersion
    {
        get { lock (_sync) return _currentRemoteVersion; }
    }
    public Guid? CurrentRemotePolicyId
    {
        get { lock (_sync) return _currentRemotePolicyId; }
    }

    public DlpPolicy Get()
    {
        lock (_sync) return _policy;
    }

    public DlpPolicy Reload()
    {
        lock (_sync)
        {
            try
            {
                if (!File.Exists(PolicyPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(PolicyPath)!);
                    _policy = CreateDefault();
                    WritePolicyAtomically(PolicyPath, _policy);
                    logger.LogWarning("Policy file did not exist. Created default policy at {PolicyPath}", PolicyPath);
                }
                else
                {
                    var json = File.ReadAllText(PolicyPath);
                    _policy = JsonSerializer.Deserialize<DlpPolicy>(json, JsonDefaults.Options) ?? CreateDefault();
                    logger.LogInformation("Loaded local policy {PolicyVersion} from {PolicyPath}", _policy.PolicyVersion, PolicyPath);
                }

                TryLoadProtectedRemoteCache();
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to load policy from {PolicyPath}; keeping last valid policy.", PolicyPath);
            }

            return _policy;
        }
    }

    public void Save(DlpPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        lock (_sync)
        {
            WritePolicyAtomically(PolicyPath, policy);
            _policy = policy;
            _currentRemoteVersion = 0;
            _currentRemotePolicyId = null;
        }
    }

    public void ApplyRemoteSnapshot(SignedPolicySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_sync)
        {
            if (snapshot.Version <= _currentRemoteVersion) return;
            var clear = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonDefaults.Options);
            var encrypted = dataProtector.Protect(clear, "CompanyDlp.RemotePolicyCache.v1");
            try
            {
                var cachePath = ResolveRemoteCachePath(snapshot.Policy.Runtime.Mode);
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                var temporary = cachePath + ".tmp";
                File.WriteAllBytes(temporary, encrypted);
                File.Move(temporary, cachePath, true);
                _policy = snapshot.Policy;
                _currentRemoteVersion = snapshot.Version;
                _currentRemotePolicyId = snapshot.PolicyId;
                logger.LogInformation("Applied remote policy {PolicyId} version {Version}", snapshot.PolicyId, snapshot.Version);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
                CryptographicOperations.ZeroMemory(encrypted);
            }
        }
    }

    private void TryLoadProtectedRemoteCache()
    {
        var cachePath = ResolveRemoteCachePath(_policy.Runtime.Mode);
        if (!File.Exists(cachePath)) return;

        byte[] protectedBytes = [];
        byte[] clear = [];
        try
        {
            protectedBytes = File.ReadAllBytes(cachePath);
            clear = dataProtector.Unprotect(protectedBytes, "CompanyDlp.RemotePolicyCache.v1");
            var snapshot = JsonSerializer.Deserialize<SignedPolicySnapshot>(clear, JsonDefaults.Options);
            if (snapshot is null || snapshot.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                logger.LogWarning("Ignoring expired or invalid cached remote policy at {CachePath}", cachePath);
                return;
            }

            _policy = snapshot.Policy;
            _currentRemoteVersion = snapshot.Version;
            _currentRemotePolicyId = snapshot.PolicyId;
            logger.LogInformation("Loaded protected cached remote policy {PolicyId} version {Version}", snapshot.PolicyId, snapshot.Version);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not load protected cached policy {CachePath}; local policy remains active.", cachePath);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    private static void WritePolicyAtomically(string path, DlpPolicy policy)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(policy, JsonDefaults.Options));
        File.Move(temporary, path, true);
    }

    private static string ResolvePolicyPath()
    {
        var explicitPath = Environment.GetEnvironmentVariable("COMPANY_DLP_POLICY_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath)) return Path.GetFullPath(explicitPath);

        var root = Environment.GetEnvironmentVariable("COMPANY_DLP_PROJECT_ROOT");
        var mode = Environment.GetEnvironmentVariable("COMPANY_DLP_MODE") ?? "Development";
        if (mode.Equals("Development", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(root))
            return Path.Combine(Path.GetFullPath(root), "config", "policy.development.json");

        if (mode.Equals("Production", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CompanyDlp", "policy.json");

        throw new InvalidOperationException(
            "Development policy path is not configured. Start with START_DEVELOPMENT.bat or set COMPANY_DLP_POLICY_PATH explicitly.");
    }

    private static string ResolveRemoteCachePath(string mode)
    {
        var root = mode.Equals("Production", StringComparison.OrdinalIgnoreCase)
            ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "CompanyDlp", "Policy", "remote-policy.cache");
    }

    private static DlpPolicy CreateDefault() => new()
    {
        Permissions = new PermissionPolicy
        {
            DefaultPermissions = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                [ActionKeys.ScreenCapture] = false,
                [ActionKeys.ScreenRecording] = false,
                [ActionKeys.ClipboardCopySensitive] = false,
                [ActionKeys.BrowserUpload] = false,
                [ActionKeys.BrowserDragDrop] = false,
                [ActionKeys.BrowserFilePaste] = false,
                [ActionKeys.BrowserImagePaste] = false,
                [ActionKeys.UsbDeviceConnect] = false,
                [ActionKeys.SoftwareInstall] = false,
                [ActionKeys.SoftwareExecuteUnapproved] = false,
                [ActionKeys.FileEncrypt] = true,
                [ActionKeys.FileDecrypt] = true
            }
        },
        SensitiveRules =
        [
            new SensitiveRule
            {
                Id = "keyword-confidential",
                Name = "Confidential keyword",
                Type = SensitiveRuleTypes.Keyword,
                Value = "confidential",
                DetectFragments = false
            },
            new SensitiveRule
            {
                Id = "any-email-address",
                Name = "Block every email address",
                Type = SensitiveRuleTypes.AnyEmail,
                Enabled = true,
                DetectFragments = true,
                BlockIndividualFragments = false
            }
        ]
    };
}
