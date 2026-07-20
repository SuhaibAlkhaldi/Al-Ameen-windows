using System.Security.Cryptography;
using System.Text.Json;
using CompanyDlp.Contracts;

namespace CompanyDlp.Service;

public sealed class PermissionLifecycleMonitor(
    PolicyStore policyStore,
    MachineDataProtector dataProtector,
    TrustedClock trustedClock,
    AuditLogger auditLogger,
    ILogger<PermissionLifecycleMonitor> logger)
{
    private const string StatePurpose = "CompanyDlp.PermissionLifecycle.v1";
    private readonly HashSet<Guid> _recordedExpiredGrantIds = [];
    private bool _loaded;
    private DateTimeOffset _nextScanAtUtc;

    public async Task TickAsync(CancellationToken cancellationToken)
    {
        var clock = trustedClock.GetSnapshot();
        var now = clock.UtcNow;
        if (now < _nextScanAtUtc) return;
        _nextScanAtUtc = now.AddSeconds(5);
        EnsureLoaded();

        var expired = policyStore.Get().Permissions.Grants
            .Where(grant => grant.GrantId != Guid.Empty)
            .Where(grant => grant.Source.Equals(PermissionSources.TemporaryGrant, StringComparison.OrdinalIgnoreCase))
            .Where(grant => grant.RevokedAtUtc is null)
            .Where(grant => grant.ExpiresAtUtc is not null && grant.ExpiresAtUtc <= now)
            .Where(grant => !_recordedExpiredGrantIds.Contains(grant.GrantId))
            .ToList();

        foreach (var grant in expired)
        {
            var context = new ClientContext
            {
                UserSid = grant.SubjectType.Equals(PermissionSubjectTypes.UserSid, StringComparison.OrdinalIgnoreCase)
                    ? grant.SubjectId
                    : "",
                Username = grant.SubjectType.Equals(PermissionSubjectTypes.Username, StringComparison.OrdinalIgnoreCase)
                    ? grant.SubjectId
                    : "",
                MachineName = Environment.MachineName,
                ClientName = "CompanyDlp.Service"
            };

            await auditLogger.WriteAsync(new AuditEvent
            {
                ActionKey = grant.ActionKey,
                EventType = "TemporaryPermissionExpired",
                Action = "temporary-permission-expired",
                Method = clock.HasServerTime ? "TrustedServerClock" : "LocalUtcEvaluation",
                Result = "audit",
                ReasonCode = "TemporaryPermissionExpired",
                PermissionGrantId = grant.GrantId,
                Details = $"subjectType={grant.SubjectType}; expiresAtUtc={grant.ExpiresAtUtc:O}; grantedBy={Sanitize(grant.GrantedBy)}"
            }, context, cancellationToken);

            _recordedExpiredGrantIds.Add(grant.GrantId);
            Save();
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        var path = ResolvePath();
        if (!File.Exists(path)) return;

        byte[] protectedBytes = [];
        byte[] clearBytes = [];
        try
        {
            protectedBytes = File.ReadAllBytes(path);
            clearBytes = dataProtector.Unprotect(protectedBytes, StatePurpose);
            var values = JsonSerializer.Deserialize<List<Guid>>(clearBytes, JsonDefaults.Options) ?? [];
            foreach (var value in values.Where(value => value != Guid.Empty))
                _recordedExpiredGrantIds.Add(value);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not load the protected permission lifecycle state.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            CryptographicOperations.ZeroMemory(clearBytes);
        }
    }

    private void Save()
    {
        var path = ResolvePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var clearBytes = JsonSerializer.SerializeToUtf8Bytes(_recordedExpiredGrantIds, JsonDefaults.Options);
        var protectedBytes = dataProtector.Protect(clearBytes, StatePurpose);
        try
        {
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllBytes(temporary, protectedBytes);
            File.Move(temporary, path, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clearBytes);
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    private string ResolvePath()
    {
        var mode = policyStore.Get().Runtime.Mode;
        var root = mode.Equals("Production", StringComparison.OrdinalIgnoreCase)
            ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "CompanyDlp", "Permissions", "lifecycle-state.bin");
    }

    private static string Sanitize(string? value)
    {
        var cleaned = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return cleaned.Length <= 200 ? cleaned : cleaned[..200];
    }
}
