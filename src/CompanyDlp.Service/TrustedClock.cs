using System.Security.Cryptography;
using System.Text.Json;
using CompanyDlp.Contracts;

namespace CompanyDlp.Service;

public sealed class TrustedClock(
    PolicyStore policyStore,
    MachineDataProtector dataProtector,
    ILogger<TrustedClock> logger) : ITrustedClock
{
    private const string ProtectionPurpose = "CompanyDlp.TrustedClock.v1";
    private static readonly TimeSpan RollbackTolerance = TimeSpan.FromMinutes(5);
    private readonly object _sync = new();
    private TrustedClockState? _state;
    private bool _loaded;

    public TrustedClockSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            EnsureLoaded();
            var localUtc = DateTimeOffset.UtcNow;
            if (_state is null)
                return new TrustedClockSnapshot(localUtc, false, false);

            var currentTick = Environment.TickCount64;
            DateTimeOffset estimatedUtc;
            if (currentTick >= _state.TickCountAtObservation)
            {
                estimatedUtc = _state.ServerTimeUtc.AddMilliseconds(currentTick - _state.TickCountAtObservation);
            }
            else
            {
                // A reboot resets TickCount64. Do not move trusted time behind the last server observation.
                estimatedUtc = _state.ServerTimeUtc;
            }

            var rollbackDetected = localUtc < estimatedUtc - RollbackTolerance;
            var effectiveUtc = localUtc > estimatedUtc ? localUtc : estimatedUtc;
            return new TrustedClockSnapshot(effectiveUtc, true, rollbackDetected);
        }
    }

    public void ObserveServerTime(DateTimeOffset serverTimeUtc)
    {
        lock (_sync)
        {
            EnsureLoaded();
            var current = GetSnapshotUnsafe();
            if (current.HasServerTime && serverTimeUtc < current.UtcNow - RollbackTolerance)
            {
                logger.LogWarning(
                    "Rejected backend server time {ServerTimeUtc} because it is behind trusted time {TrustedTimeUtc}.",
                    serverTimeUtc,
                    current.UtcNow);
                return;
            }

            _state = new TrustedClockState
            {
                ServerTimeUtc = serverTimeUtc.ToUniversalTime(),
                LocalUtcAtObservation = DateTimeOffset.UtcNow,
                TickCountAtObservation = Environment.TickCount64
            };
            Save();
        }
    }

    private TrustedClockSnapshot GetSnapshotUnsafe()
    {
        var localUtc = DateTimeOffset.UtcNow;
        if (_state is null) return new TrustedClockSnapshot(localUtc, false, false);

        var currentTick = Environment.TickCount64;
        var estimatedUtc = currentTick >= _state.TickCountAtObservation
            ? _state.ServerTimeUtc.AddMilliseconds(currentTick - _state.TickCountAtObservation)
            : _state.ServerTimeUtc;
        return new TrustedClockSnapshot(
            localUtc > estimatedUtc ? localUtc : estimatedUtc,
            true,
            localUtc < estimatedUtc - RollbackTolerance);
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
            clearBytes = dataProtector.Unprotect(protectedBytes, ProtectionPurpose);
            _state = JsonSerializer.Deserialize<TrustedClockState>(clearBytes, JsonDefaults.Options);
        }
        catch (Exception exception)
        {
            _state = null;
            logger.LogWarning(exception, "Could not load the protected trusted-clock state.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            CryptographicOperations.ZeroMemory(clearBytes);
        }
    }

    private void Save()
    {
        if (_state is null) return;
        var path = ResolvePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var clearBytes = JsonSerializer.SerializeToUtf8Bytes(_state, JsonDefaults.Options);
        var protectedBytes = dataProtector.Protect(clearBytes, ProtectionPurpose);
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
        return Path.Combine(root, "CompanyDlp", "Agent", "trusted-clock.bin");
    }

    private sealed class TrustedClockState
    {
        public DateTimeOffset ServerTimeUtc { get; set; }
        public DateTimeOffset LocalUtcAtObservation { get; set; }
        public long TickCountAtObservation { get; set; }
    }
}
