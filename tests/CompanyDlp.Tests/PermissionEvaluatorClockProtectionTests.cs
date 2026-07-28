using CompanyDlp.Contracts;
using CompanyDlp.Core;
using Xunit;

namespace CompanyDlp.Tests;

// Verifies the clock-rollback protection in PermissionEvaluator.Evaluate: in Production mode, a
// temporary grant is denied if trusted server time isn't available or a rollback was detected - this
// exists specifically so winding back the local machine clock can't be used to keep a temporary grant
// "alive" past its real expiry. Uses a controlled fake ITrustedClock (real PermissionEvaluator code,
// deterministic snapshot input) rather than actually changing the OS clock, which would have real
// machine-wide side effects (TLS validation, other services, scheduled tasks) for a live dev machine.
public sealed class PermissionEvaluatorClockProtectionTests
{
    private readonly AgentIdentity _identity = new()
    {
        DeviceId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        MachineName = "TEST-PC"
    };

    private readonly ClientContext _context = new()
    {
        UserSid = "S-1-5-21-1000",
        Username = "TEST\\employee",
        MachineName = "TEST-PC"
    };

    private sealed class FakeTrustedClock(DateTimeOffset utcNow, bool hasServerTime, bool rollbackDetected) : ITrustedClock
    {
        public TrustedClockSnapshot GetSnapshot() => new(utcNow, hasServerTime, rollbackDetected);
    }

    private DlpPolicy CreatePolicy(string mode) => new()
    {
        Runtime = new RuntimePolicy { Mode = mode },
        Permissions = new PermissionPolicy
        {
            DefaultPermissions = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                [ActionKeys.UsbDeviceConnect] = false
            }
        }
    };

    private PermissionGrant CreateTemporaryGrant(DateTimeOffset now) => new()
    {
        ActionKey = ActionKeys.UsbDeviceConnect,
        Allowed = true,
        SubjectType = PermissionSubjectTypes.UserSid,
        SubjectId = _context.UserSid,
        Source = PermissionSources.TemporaryGrant,
        StartsAtUtc = now.AddMinutes(-1),
        ExpiresAtUtc = now.AddMinutes(10),
        Priority = 500
    };

    private PermissionGrant CreatePermanentGrant() => new()
    {
        ActionKey = ActionKeys.UsbDeviceConnect,
        Allowed = true,
        SubjectType = PermissionSubjectTypes.UserSid,
        SubjectId = _context.UserSid,
        Source = PermissionSources.PermanentPolicy,
        Priority = 500
    };

    [Fact]
    public void Production_TemporaryGrant_ClockRollbackDetected_IsDenied()
    {
        var now = DateTimeOffset.UtcNow;
        var policy = CreatePolicy("Production");
        policy.Permissions.Grants.Add(CreateTemporaryGrant(now));
        var clock = new FakeTrustedClock(now, hasServerTime: true, rollbackDetected: true);

        var evaluator = new PermissionEvaluator(classificationCache: null, trustedClock: clock);
        var result = evaluator.Evaluate(policy, ActionKeys.UsbDeviceConnect, _context, _identity, now);

        Assert.False(result.IsAllowed);
        Assert.Equal("ClockRollbackDetected", result.ReasonCode);
    }

    [Fact]
    public void Production_TemporaryGrant_NoTrustedServerTimeYet_IsDenied()
    {
        var now = DateTimeOffset.UtcNow;
        var policy = CreatePolicy("Production");
        policy.Permissions.Grants.Add(CreateTemporaryGrant(now));
        var clock = new FakeTrustedClock(now, hasServerTime: false, rollbackDetected: false);

        var evaluator = new PermissionEvaluator(classificationCache: null, trustedClock: clock);
        var result = evaluator.Evaluate(policy, ActionKeys.UsbDeviceConnect, _context, _identity, now);

        Assert.False(result.IsAllowed);
        Assert.Equal("TrustedTimeUnavailable", result.ReasonCode);
    }

    [Fact]
    public void Production_TemporaryGrant_HealthyTrustedClock_IsAllowed()
    {
        var now = DateTimeOffset.UtcNow;
        var policy = CreatePolicy("Production");
        policy.Permissions.Grants.Add(CreateTemporaryGrant(now));
        var clock = new FakeTrustedClock(now, hasServerTime: true, rollbackDetected: false);

        var evaluator = new PermissionEvaluator(classificationCache: null, trustedClock: clock);
        var result = evaluator.Evaluate(policy, ActionKeys.UsbDeviceConnect, _context, _identity, now);

        Assert.True(result.IsAllowed);
        Assert.Equal("TemporaryPermissionActive", result.ReasonCode);
    }

    // Documents a real, deliberate scope boundary discovered while verifying this live: the guard
    // only fires in Production. A Development-mode agent (e.g. this project's own dev/demo stack)
    // does NOT enforce clock-rollback protection at all - confirmed here rather than assumed, since
    // it explains why live-testing this against the actual running dev agent would show no effect.
    [Fact]
    public void DevelopmentMode_TemporaryGrant_ClockRollbackDetected_IsNotDenied()
    {
        var now = DateTimeOffset.UtcNow;
        var policy = CreatePolicy("Development");
        policy.Permissions.Grants.Add(CreateTemporaryGrant(now));
        var clock = new FakeTrustedClock(now, hasServerTime: true, rollbackDetected: true);

        var evaluator = new PermissionEvaluator(classificationCache: null, trustedClock: clock);
        var result = evaluator.Evaluate(policy, ActionKeys.UsbDeviceConnect, _context, _identity, now);

        Assert.True(result.IsAllowed);
        Assert.Equal("TemporaryPermissionActive", result.ReasonCode);
    }

    // The guard is scoped to `isTemporary` grants specifically (matches item 1: permanent grants
    // aren't time-bounded at all, so clock manipulation has nothing to exploit there).
    [Fact]
    public void Production_PermanentGrant_ClockRollbackDetected_IsStillAllowed()
    {
        var now = DateTimeOffset.UtcNow;
        var policy = CreatePolicy("Production");
        policy.Permissions.Grants.Add(CreatePermanentGrant());
        var clock = new FakeTrustedClock(now, hasServerTime: true, rollbackDetected: true);

        var evaluator = new PermissionEvaluator(classificationCache: null, trustedClock: clock);
        var result = evaluator.Evaluate(policy, ActionKeys.UsbDeviceConnect, _context, _identity, now);

        Assert.True(result.IsAllowed);
    }
}
