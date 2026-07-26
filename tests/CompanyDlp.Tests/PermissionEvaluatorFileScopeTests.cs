using CompanyDlp.Contracts;
using CompanyDlp.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CompanyDlp.Tests;

public sealed class PermissionEvaluatorFileScopeTests
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
        MachineName = "TEST-PC",
        WindowsSessionId = 2
    };

    private static FileClassificationCache NewCache()
    {
        var store = new PolicyStore(new MachineDataProtector(), NullLogger<PolicyStore>.Instance);
        return new FileClassificationCache(store, NullLogger<FileClassificationCache>.Instance);
    }

    private static string NewFileHash() => Guid.NewGuid().ToString("N").PadRight(64, '0');

    private DlpPolicy CreatePolicy() => new()
    {
        Permissions = new PermissionPolicy
        {
            DefaultPermissions = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                [ActionKeys.BrowserUpload] = false
            }
        }
    };

    [Fact]
    public void FileHashGrant_AllowsExactMatchingFile()
    {
        var now = DateTimeOffset.UtcNow;
        var hash = NewFileHash();
        var policy = CreatePolicy();
        policy.Permissions.Grants.Add(new PermissionGrant
        {
            ActionKey = ActionKeys.BrowserUpload,
            Allowed = true,
            SubjectType = PermissionSubjectTypes.UserSid,
            SubjectId = _context.UserSid,
            Source = PermissionSources.TemporaryGrant,
            StartsAtUtc = now.AddMinutes(-1),
            ExpiresAtUtc = now.AddMinutes(10),
            FileHash = hash,
            Priority = 500
        });

        var evaluator = new PermissionEvaluator(NewCache());
        var result = evaluator.Evaluate(policy, ActionKeys.BrowserUpload, _context, _identity, now, hash);

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void FileHashGrant_DoesNotAllowADifferentFile()
    {
        var now = DateTimeOffset.UtcNow;
        var policy = CreatePolicy();
        policy.Permissions.Grants.Add(new PermissionGrant
        {
            ActionKey = ActionKeys.BrowserUpload,
            Allowed = true,
            SubjectType = PermissionSubjectTypes.UserSid,
            SubjectId = _context.UserSid,
            Source = PermissionSources.TemporaryGrant,
            StartsAtUtc = now.AddMinutes(-1),
            ExpiresAtUtc = now.AddMinutes(10),
            FileHash = NewFileHash(),
            Priority = 500
        });

        var evaluator = new PermissionEvaluator(NewCache());
        var result = evaluator.Evaluate(policy, ActionKeys.BrowserUpload, _context, _identity, now, NewFileHash());

        Assert.False(result.IsAllowed);
        Assert.Equal("GlobalDefaultDeny", result.ReasonCode);
    }

    [Fact]
    public void ClassificationTierGrant_CoversAFileClassifiedAtOrBelowThatTier()
    {
        var now = DateTimeOffset.UtcNow;
        var hash = NewFileHash();
        var cache = NewCache();
        cache.Set(new CachedFileClassification(hash, ClassificationTiers.Internal, "AiClassified", now));

        var policy = CreatePolicy();
        policy.Permissions.Grants.Add(new PermissionGrant
        {
            ActionKey = ActionKeys.BrowserUpload,
            Allowed = true,
            SubjectType = PermissionSubjectTypes.UserSid,
            SubjectId = _context.UserSid,
            Source = PermissionSources.TemporaryGrant,
            StartsAtUtc = now.AddMinutes(-1),
            ExpiresAtUtc = now.AddMinutes(10),
            ClassificationTier = ClassificationTiers.Secret,
            Priority = 500
        });

        var evaluator = new PermissionEvaluator(cache);
        var result = evaluator.Evaluate(policy, ActionKeys.BrowserUpload, _context, _identity, now, hash);

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void ClassificationTierGrant_DoesNotCoverAMoreSensitiveFile()
    {
        var now = DateTimeOffset.UtcNow;
        var hash = NewFileHash();
        var cache = NewCache();
        cache.Set(new CachedFileClassification(hash, ClassificationTiers.VerySecret, "AiClassified", now));

        var policy = CreatePolicy();
        policy.Permissions.Grants.Add(new PermissionGrant
        {
            ActionKey = ActionKeys.BrowserUpload,
            Allowed = true,
            SubjectType = PermissionSubjectTypes.UserSid,
            SubjectId = _context.UserSid,
            Source = PermissionSources.TemporaryGrant,
            StartsAtUtc = now.AddMinutes(-1),
            ExpiresAtUtc = now.AddMinutes(10),
            ClassificationTier = ClassificationTiers.Secret,
            Priority = 500
        });

        var evaluator = new PermissionEvaluator(cache);
        var result = evaluator.Evaluate(policy, ActionKeys.BrowserUpload, _context, _identity, now, hash);

        Assert.False(result.IsAllowed);
    }

    [Fact]
    public void UncachedFile_FailsClosedEvenAgainstATierGrant()
    {
        var now = DateTimeOffset.UtcNow;
        var hash = NewFileHash(); // never Set() in the cache - simulates a not-yet-classified file
        var policy = CreatePolicy();
        policy.Permissions.Grants.Add(new PermissionGrant
        {
            ActionKey = ActionKeys.BrowserUpload,
            Allowed = true,
            SubjectType = PermissionSubjectTypes.UserSid,
            SubjectId = _context.UserSid,
            Source = PermissionSources.TemporaryGrant,
            StartsAtUtc = now.AddMinutes(-1),
            ExpiresAtUtc = now.AddMinutes(10),
            ClassificationTier = ClassificationTiers.Secret,
            Priority = 500
        });

        var evaluator = new PermissionEvaluator(NewCache());
        var result = evaluator.Evaluate(policy, ActionKeys.BrowserUpload, _context, _identity, now, hash);

        Assert.False(result.IsAllowed);
    }

    [Fact]
    public void ExactFileHashGrant_TakesPrecedenceOverATierGrantForTheSameFile()
    {
        var now = DateTimeOffset.UtcNow;
        var hash = NewFileHash();
        var cache = NewCache();
        cache.Set(new CachedFileClassification(hash, ClassificationTiers.VerySecret, "AiClassified", now));

        var policy = CreatePolicy();
        // Tier grant alone would deny this VerySecret file - but a narrower, exact-hash grant for
        // this specific file also exists and must win regardless of grant insertion order.
        policy.Permissions.Grants.Add(new PermissionGrant
        {
            ActionKey = ActionKeys.BrowserUpload,
            Allowed = false,
            SubjectType = PermissionSubjectTypes.UserSid,
            SubjectId = _context.UserSid,
            Source = PermissionSources.TemporaryGrant,
            StartsAtUtc = now.AddMinutes(-1),
            ExpiresAtUtc = now.AddMinutes(10),
            ClassificationTier = ClassificationTiers.Secret,
            Priority = 999
        });
        policy.Permissions.Grants.Add(new PermissionGrant
        {
            ActionKey = ActionKeys.BrowserUpload,
            Allowed = true,
            SubjectType = PermissionSubjectTypes.UserSid,
            SubjectId = _context.UserSid,
            Source = PermissionSources.TemporaryGrant,
            StartsAtUtc = now.AddMinutes(-1),
            ExpiresAtUtc = now.AddMinutes(10),
            FileHash = hash,
            Priority = 100
        });

        var evaluator = new PermissionEvaluator(cache);
        var result = evaluator.Evaluate(policy, ActionKeys.BrowserUpload, _context, _identity, now, hash);

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void PublicFile_IsAllowedWithNoGrantAtAll()
    {
        var now = DateTimeOffset.UtcNow;
        var hash = NewFileHash();
        var cache = NewCache();
        cache.Set(new CachedFileClassification(hash, ClassificationTiers.Public, "AiClassified", now));

        // No grants at all, and the org default for browser.upload is Deny - only the Public
        // classification itself should allow this through.
        var policy = CreatePolicy();

        var evaluator = new PermissionEvaluator(cache);
        var result = evaluator.Evaluate(policy, ActionKeys.BrowserUpload, _context, _identity, now, hash);

        Assert.True(result.IsAllowed);
        Assert.Equal("PublicClassification", result.ReasonCode);
    }

    [Fact]
    public void EmergencyDeny_StillWinsOverAPublicFile()
    {
        var now = DateTimeOffset.UtcNow;
        var hash = NewFileHash();
        var cache = NewCache();
        cache.Set(new CachedFileClassification(hash, ClassificationTiers.Public, "AiClassified", now));

        var policy = CreatePolicy();
        policy.Permissions.Grants.Add(new PermissionGrant
        {
            ActionKey = ActionKeys.BrowserUpload,
            Allowed = false,
            SubjectType = PermissionSubjectTypes.Global,
            SubjectId = "*",
            Source = PermissionSources.EmergencyDeny,
            StartsAtUtc = now.AddMinutes(-1),
            Priority = 1
        });

        var evaluator = new PermissionEvaluator(cache);
        var result = evaluator.Evaluate(policy, ActionKeys.BrowserUpload, _context, _identity, now, hash);

        Assert.False(result.IsAllowed);
        Assert.Equal("EmergencyDeny", result.ReasonCode);
    }

    [Fact]
    public void UnscopedGrant_StillMatchesWhenNoFileHashIsGiven()
    {
        var now = DateTimeOffset.UtcNow;
        var policy = CreatePolicy();
        policy.Permissions.Grants.Add(new PermissionGrant
        {
            ActionKey = ActionKeys.BrowserUpload,
            Allowed = true,
            SubjectType = PermissionSubjectTypes.UserSid,
            SubjectId = _context.UserSid,
            Source = PermissionSources.TemporaryGrant,
            StartsAtUtc = now.AddMinutes(-1),
            ExpiresAtUtc = now.AddMinutes(10),
            Priority = 500
        });

        var evaluator = new PermissionEvaluator(NewCache());
        var result = evaluator.Evaluate(policy, ActionKeys.BrowserUpload, _context, _identity, now);

        Assert.True(result.IsAllowed);
    }
}
