using CompanyDlp.Contracts;
using CompanyDlp.Core;
using Xunit;

namespace CompanyDlp.Tests;

public sealed class PermissionEvaluatorTests
{
    private readonly PermissionEvaluator _evaluator = new();
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
        MachineName = "TEST-PC",
        WindowsSessionId = 2
    };

    [Fact]
    public void DefaultDeny_IsUsedWhenNoGrantMatches()
    {
        var policy = CreatePolicy(defaultAllowed: false);
        var result = _evaluator.Evaluate(policy, ActionKeys.ScreenCapture, _context, _identity, DateTimeOffset.UtcNow);
        Assert.False(result.IsAllowed);
        Assert.Equal("GlobalDefaultDeny", result.ReasonCode);
    }

    [Fact]
    public void ActiveTemporaryUserGrant_OverridesDefaultDeny()
    {
        var now = DateTimeOffset.UtcNow;
        var policy = CreatePolicy(defaultAllowed: false);
        var grant = new PermissionGrant
        {
            ActionKey = ActionKeys.ScreenCapture,
            Allowed = true,
            SubjectType = PermissionSubjectTypes.UserSid,
            SubjectId = _context.UserSid,
            Source = PermissionSources.TemporaryGrant,
            StartsAtUtc = now.AddMinutes(-1),
            ExpiresAtUtc = now.AddMinutes(10),
            Priority = 500
        };
        policy.Permissions.Grants.Add(grant);

        var result = _evaluator.Evaluate(policy, ActionKeys.ScreenCapture, _context, _identity, now);
        Assert.True(result.IsAllowed);
        Assert.Equal(grant.GrantId, result.PermissionGrantId);
        Assert.Equal("TemporaryPermissionActive", result.ReasonCode);
    }

    [Fact]
    public void ExpiredTemporaryGrant_IsIgnoredAutomatically()
    {
        var now = DateTimeOffset.UtcNow;
        var policy = CreatePolicy(defaultAllowed: false);
        policy.Permissions.Grants.Add(new PermissionGrant
        {
            ActionKey = ActionKeys.ScreenCapture,
            Allowed = true,
            SubjectType = PermissionSubjectTypes.UserSid,
            SubjectId = _context.UserSid,
            Source = PermissionSources.TemporaryGrant,
            StartsAtUtc = now.AddHours(-1),
            ExpiresAtUtc = now.AddSeconds(-1),
            Priority = 500
        });

        var result = _evaluator.Evaluate(policy, ActionKeys.ScreenCapture, _context, _identity, now);
        Assert.False(result.IsAllowed);
        Assert.Equal("GlobalDefaultDeny", result.ReasonCode);
    }

    [Fact]
    public void EmergencyDeny_WinsOverHigherPriorityAllow()
    {
        var now = DateTimeOffset.UtcNow;
        var policy = CreatePolicy(defaultAllowed: true);
        policy.Permissions.Grants.Add(new PermissionGrant
        {
            ActionKey = ActionKeys.ScreenRecording,
            Allowed = true,
            SubjectType = PermissionSubjectTypes.UserSid,
            SubjectId = _context.UserSid,
            Source = PermissionSources.TemporaryGrant,
            StartsAtUtc = now.AddMinutes(-1),
            ExpiresAtUtc = now.AddMinutes(10),
            Priority = 9999
        });
        policy.Permissions.Grants.Add(new PermissionGrant
        {
            ActionKey = ActionKeys.ScreenRecording,
            Allowed = false,
            SubjectType = PermissionSubjectTypes.Global,
            SubjectId = "*",
            Source = PermissionSources.EmergencyDeny,
            StartsAtUtc = now.AddMinutes(-1),
            Priority = 1
        });

        var result = _evaluator.Evaluate(policy, ActionKeys.ScreenRecording, _context, _identity, now);
        Assert.False(result.IsAllowed);
        Assert.Equal("EmergencyDeny", result.ReasonCode);
    }

    // FileWatermarkDisable is deliberately different from every other tier-scoped action (including
    // FilePrint, which is "this tier and anything less sensitive") - see
    // PermissionEvaluator.ActionsRequiringExactTierMatch's comment. A Secret-tier grant here must
    // cover Secret files only, not silently widen to also hide the watermark on that employee's
    // Internal/Public files.
    [Fact]
    public void FileWatermarkDisable_TierScopedGrant_DoesNotCoverLessSensitiveTiers()
    {
        var now = DateTimeOffset.UtcNow;
        var policy = CreatePolicy(defaultAllowed: false);
        policy.Permissions.Grants.Add(new PermissionGrant
        {
            ActionKey = ActionKeys.FileWatermarkDisable,
            Allowed = true,
            SubjectType = PermissionSubjectTypes.UserSid,
            SubjectId = _context.UserSid,
            Source = PermissionSources.PermanentPolicy,
            ClassificationTier = ClassificationTiers.Secret,
            StartsAtUtc = now.AddMinutes(-1)
        });

        var secretResult = _evaluator.Evaluate(policy, ActionKeys.FileWatermarkDisable, _context, _identity, now, knownClassificationTier: ClassificationTiers.Secret);
        Assert.True(secretResult.IsAllowed);

        var internalResult = _evaluator.Evaluate(policy, ActionKeys.FileWatermarkDisable, _context, _identity, now, knownClassificationTier: ClassificationTiers.Internal);
        Assert.False(internalResult.IsAllowed);

        var publicResult = _evaluator.Evaluate(policy, ActionKeys.FileWatermarkDisable, _context, _identity, now, knownClassificationTier: ClassificationTiers.Public);
        Assert.False(publicResult.IsAllowed);
    }

    // Contrast case: FilePrint keeps the "this tier and anything less sensitive" widening every
    // other tier-scoped action uses - a Secret-tier print grant also covers Public files.
    [Fact]
    public void FilePrint_TierScopedGrant_AlsoCoversLessSensitiveTiers()
    {
        var now = DateTimeOffset.UtcNow;
        var policy = CreatePolicy(defaultAllowed: false);
        policy.Permissions.Grants.Add(new PermissionGrant
        {
            ActionKey = ActionKeys.FilePrint,
            Allowed = true,
            SubjectType = PermissionSubjectTypes.UserSid,
            SubjectId = _context.UserSid,
            Source = PermissionSources.PermanentPolicy,
            ClassificationTier = ClassificationTiers.Secret,
            StartsAtUtc = now.AddMinutes(-1)
        });

        var publicResult = _evaluator.Evaluate(policy, ActionKeys.FilePrint, _context, _identity, now, knownClassificationTier: ClassificationTiers.Public);
        Assert.True(publicResult.IsAllowed);
    }

    private static DlpPolicy CreatePolicy(bool defaultAllowed) => new()
    {
        Permissions = new PermissionPolicy
        {
            DefaultPermissions = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                [ActionKeys.ScreenCapture] = defaultAllowed,
                [ActionKeys.ScreenRecording] = defaultAllowed
            }
        }
    };
}
