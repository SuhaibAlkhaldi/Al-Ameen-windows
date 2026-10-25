using CompanyDlp.Contracts;
using CompanyDlp.Core;
using Xunit;

namespace CompanyDlp.Tests;

// Covers PermissionEvaluator.EvaluateAllowedKeysByPrefix - the browser-extension-approval feature's
// one genuinely new piece of agent-side decision logic (everything else reuses the existing
// single-key Evaluate() this method is built on top of).
public sealed class PermissionEvaluatorEvaluateAllowedKeysByPrefixTests
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

    private static DlpPolicy EmptyPolicy() => new() { Permissions = new PermissionPolicy() };

    private PermissionGrant AllowGrant(string actionKey) => new()
    {
        ActionKey = actionKey,
        Allowed = true,
        SubjectType = PermissionSubjectTypes.UserSid,
        SubjectId = _context.UserSid,
        Source = PermissionSources.PermanentPolicy,
        StartsAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
        Priority = 500
    };

    [Fact]
    public void TwoApprovedExtensions_ReturnsBothKeys()
    {
        var policy = EmptyPolicy();
        policy.Permissions.Grants.Add(AllowGrant("extension.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        policy.Permissions.Grants.Add(AllowGrant("extension.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));

        var result = _evaluator.EvaluateAllowedKeysByPrefix(policy, "extension.", _context, _identity, DateTimeOffset.UtcNow);

        Assert.Equal(2, result.Count);
        Assert.Contains("extension.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", result);
        Assert.Contains("extension.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", result);
    }

    [Fact]
    public void PrefixMatchIsCaseInsensitive()
    {
        var policy = EmptyPolicy();
        policy.Permissions.Grants.Add(AllowGrant("EXTENSION.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));

        var result = _evaluator.EvaluateAllowedKeysByPrefix(policy, "extension.", _context, _identity, DateTimeOffset.UtcNow);

        Assert.Single(result);
    }

    [Fact]
    public void DuplicateGrantsForSameKey_ReturnsKeyOnce()
    {
        var policy = EmptyPolicy();
        policy.Permissions.Grants.Add(AllowGrant("extension.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        policy.Permissions.Grants.Add(AllowGrant("extension.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));

        var result = _evaluator.EvaluateAllowedKeysByPrefix(policy, "extension.", _context, _identity, DateTimeOffset.UtcNow);

        Assert.Single(result);
    }

    [Fact]
    public void ExtensionUnrestrictedGrant_IsExcludedFromResults()
    {
        var policy = EmptyPolicy();
        policy.Permissions.Grants.Add(AllowGrant(ActionKeys.ExtensionUnrestricted));
        policy.Permissions.Grants.Add(AllowGrant("extension.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));

        var result = _evaluator.EvaluateAllowedKeysByPrefix(policy, "extension.", _context, _identity, DateTimeOffset.UtcNow);

        Assert.Single(result);
        Assert.DoesNotContain(ActionKeys.ExtensionUnrestricted, result);
    }

    [Fact]
    public void RevokedGrant_IsExcludedFromResults()
    {
        var policy = EmptyPolicy();
        var grant = AllowGrant("extension.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        grant.RevokedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1);
        policy.Permissions.Grants.Add(grant);

        var result = _evaluator.EvaluateAllowedKeysByPrefix(policy, "extension.", _context, _identity, DateTimeOffset.UtcNow);

        Assert.Empty(result);
    }

    [Fact]
    public void ExpiredTemporaryGrant_IsExcludedFromResults()
    {
        var policy = EmptyPolicy();
        var grant = AllowGrant("extension.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        grant.Source = PermissionSources.TemporaryGrant;
        grant.ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1);
        policy.Permissions.Grants.Add(grant);

        var result = _evaluator.EvaluateAllowedKeysByPrefix(policy, "extension.", _context, _identity, DateTimeOffset.UtcNow);

        Assert.Empty(result);
    }

    [Fact]
    public void EmergencyDenyForOneExtension_DoesNotSuppressAnotherExtensionsOwnAllow()
    {
        var policy = EmptyPolicy();
        policy.Permissions.Grants.Add(AllowGrant("extension.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));

        var denyGrant = new PermissionGrant
        {
            ActionKey = "extension.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            Allowed = false,
            SubjectType = PermissionSubjectTypes.UserSid,
            SubjectId = _context.UserSid,
            Source = PermissionSources.EmergencyDeny,
            StartsAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            Priority = 1000
        };
        policy.Permissions.Grants.Add(denyGrant);

        var result = _evaluator.EvaluateAllowedKeysByPrefix(policy, "extension.", _context, _identity, DateTimeOffset.UtcNow);

        Assert.Single(result);
        Assert.Contains("extension.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", result);
    }

    [Fact]
    public void NoMatchingGrants_ReturnsEmpty()
    {
        var policy = EmptyPolicy();
        policy.Permissions.Grants.Add(AllowGrant("usb.storage"));

        var result = _evaluator.EvaluateAllowedKeysByPrefix(policy, "extension.", _context, _identity, DateTimeOffset.UtcNow);

        Assert.Empty(result);
    }
}
