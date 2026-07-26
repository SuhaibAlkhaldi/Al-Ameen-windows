using CompanyDlp.Contracts;
using CompanyDlp.Core;
using CompanyDlp.Service;
using Xunit;

namespace CompanyDlp.Tests;

public sealed class BrowserPolicyManagerTests
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

    // This is the exact logic ResolveDisableGameCapture (and ResolveBlockDownloads) apply on top of the
    // grant decision, for the ScreenRecording/DisableWindowsGameCapture Production-mode registry fix:
    // Production enforcement must consult the grant, not the raw policy default.

    [Fact]
    public void NoGrant_ScreenRecordingDefaultDeny_StillBlocksGameCapture()
    {
        var policy = CreatePolicy(ActionKeys.ScreenRecording, defaultAllowed: false);
        var shouldBlock = BrowserPolicyManager.ShouldBlockForMissingGrant(
            policy, _evaluator, ActionKeys.ScreenRecording, _context, _identity);
        Assert.True(shouldBlock);
    }

    [Fact]
    public void ActivePermanentGrant_RelaxesGameCaptureBlock()
    {
        var policy = CreatePolicy(ActionKeys.ScreenRecording, defaultAllowed: false);
        policy.Permissions.Grants.Add(new PermissionGrant
        {
            ActionKey = ActionKeys.ScreenRecording,
            Allowed = true,
            SubjectType = PermissionSubjectTypes.UserSid,
            SubjectId = _context.UserSid,
            Source = PermissionSources.PermanentPolicy,
            StartsAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            Priority = 500
        });

        var shouldBlock = BrowserPolicyManager.ShouldBlockForMissingGrant(
            policy, _evaluator, ActionKeys.ScreenRecording, _context, _identity);
        Assert.False(shouldBlock);
    }

    [Fact]
    public void RevokedGrant_ReblocksGameCapture()
    {
        var policy = CreatePolicy(ActionKeys.ScreenRecording, defaultAllowed: false);
        policy.Permissions.Grants.Add(new PermissionGrant
        {
            ActionKey = ActionKeys.ScreenRecording,
            Allowed = true,
            SubjectType = PermissionSubjectTypes.UserSid,
            SubjectId = _context.UserSid,
            Source = PermissionSources.PermanentPolicy,
            StartsAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
            RevokedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            Priority = 500
        });

        var shouldBlock = BrowserPolicyManager.ShouldBlockForMissingGrant(
            policy, _evaluator, ActionKeys.ScreenRecording, _context, _identity);
        Assert.True(shouldBlock);
    }

    [Fact]
    public void ExpiredTemporaryGrant_ReblocksGameCaptureAutomatically()
    {
        var policy = CreatePolicy(ActionKeys.ScreenRecording, defaultAllowed: false);
        policy.Permissions.Grants.Add(new PermissionGrant
        {
            ActionKey = ActionKeys.ScreenRecording,
            Allowed = true,
            SubjectType = PermissionSubjectTypes.UserSid,
            SubjectId = _context.UserSid,
            Source = PermissionSources.TemporaryGrant,
            StartsAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1),
            Priority = 500
        });

        var shouldBlock = BrowserPolicyManager.ShouldBlockForMissingGrant(
            policy, _evaluator, ActionKeys.ScreenRecording, _context, _identity);
        Assert.True(shouldBlock);
    }

    private static DlpPolicy CreatePolicy(string actionKey, bool defaultAllowed) => new()
    {
        Permissions = new PermissionPolicy
        {
            DefaultPermissions = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                [actionKey] = defaultAllowed
            }
        }
    };
}
