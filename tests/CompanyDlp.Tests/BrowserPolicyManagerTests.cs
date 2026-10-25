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

    // Covers the browser-extension-approval feature's two decision points that live in
    // BrowserPolicyManager itself: extension.unrestricted (read via the same ShouldBlockForMissingGrant
    // helper the Screen/Browser-download checks above already use) and BuildWritePlan's unaffected
    // Forcelist/Blocklist decision given that resolved value.

    [Fact]
    public void NoUnrestrictedGrant_ExtensionUnrestrictedIsBlocked()
    {
        var policy = CreatePolicy(ActionKeys.ExtensionUnrestricted, defaultAllowed: false);
        var shouldBlock = BrowserPolicyManager.ShouldBlockForMissingGrant(
            policy, _evaluator, ActionKeys.ExtensionUnrestricted, _context, _identity);
        Assert.True(shouldBlock);
    }

    [Fact]
    public void ActiveUnrestrictedGrant_ExtensionUnrestrictedIsAllowed()
    {
        var policy = CreatePolicy(ActionKeys.ExtensionUnrestricted, defaultAllowed: false);
        policy.Permissions.Grants.Add(new PermissionGrant
        {
            ActionKey = ActionKeys.ExtensionUnrestricted,
            Allowed = true,
            SubjectType = PermissionSubjectTypes.DeviceId,
            SubjectId = _identity.DeviceId.ToString(),
            Source = PermissionSources.PermanentPolicy,
            StartsAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            Priority = 500
        });

        var shouldBlock = BrowserPolicyManager.ShouldBlockForMissingGrant(
            policy, _evaluator, ActionKeys.ExtensionUnrestricted, _context, _identity);
        Assert.False(shouldBlock);
    }

    [Theory]
    [InlineData(ExtensionForceInstallStatus.Valid, true, false, true)]   // valid id + block requested + NOT unrestricted -> still blocks others
    [InlineData(ExtensionForceInstallStatus.Valid, true, true, false)]  // valid id + block requested BUT unrestricted overrides it -> does not block others
    [InlineData(ExtensionForceInstallStatus.Valid, false, false, false)] // block not requested at all -> never blocks others
    [InlineData(ExtensionForceInstallStatus.Invalid, true, false, false)] // misconfigured id -> never force-installs or blocks, regardless of the flag
    public void BuildWritePlan_BlockOthersReflectsUnrestrictedOverride(
        ExtensionForceInstallStatus status, bool blockUnapprovedExtensions, bool extensionsUnrestricted, bool expectedWriteBlocklist)
    {
        var effectiveBlockOthers = blockUnapprovedExtensions && !extensionsUnrestricted;
        var plan = BrowserPolicyManager.BuildWritePlan(status, effectiveBlockOthers);

        Assert.Equal(expectedWriteBlocklist, plan.WriteBlocklist);
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
