using CompanyDlp.Contracts;

namespace CompanyDlp.Core;

public sealed class PermissionEvaluator(FileClassificationCache? classificationCache = null, ITrustedClock? trustedClock = null)
{
    public PermissionDecision Evaluate(
        DlpPolicy policy,
        string actionKey,
        ClientContext context,
        AgentIdentity identity,
        DateTimeOffset nowUtc,
        string? fileHash = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionKey);
        context ??= new ClientContext();
        var clock = trustedClock?.GetSnapshot();
        var evaluationTimeUtc = clock?.UtcNow ?? nowUtc;

        // A file/tier-scoped grant only ever matters when this evaluation is actually about a
        // specific file; a cache miss for a known fileHash is treated as the most sensitive tier
        // (fail-closed) rather than "no classification restriction".
        var fileRank = fileHash is null
            ? (int?)null
            : ClassificationTiers.RankOf(classificationCache?.TryGet(fileHash)?.Classification ?? ClassificationTiers.VerySecret);

        var candidates = policy.Permissions.Grants
            .Where(grant => grant.ActionKey.Equals(actionKey, StringComparison.OrdinalIgnoreCase))
            .Where(grant => MatchesSubject(grant, context, identity))
            .Where(grant => IsActive(grant, evaluationTimeUtc))
            .Where(grant => MatchesFileScope(grant, fileHash, fileRank))
            .OrderByDescending(grant => IsEmergencyDeny(grant))
            .ThenByDescending(grant => FileScopeSpecificity(grant, fileHash))
            .ThenByDescending(grant => grant.Priority)
            .ThenByDescending(grant => SubjectSpecificity(grant.SubjectType))
            .ThenByDescending(grant => grant.CreatedAtUtc)
            .ToList();

        var selected = candidates.FirstOrDefault();
        if (selected is not null)
        {
            var isTemporary = selected.Source.Equals(PermissionSources.TemporaryGrant, StringComparison.OrdinalIgnoreCase);
            var production = policy.Runtime.Mode.Equals("Production", StringComparison.OrdinalIgnoreCase);
            if (isTemporary && production && clock is not null && (!clock.HasServerTime || clock.ClockRollbackDetected))
            {
                return new PermissionDecision
                {
                    ActionKey = actionKey,
                    IsAllowed = false,
                    ReasonCode = clock.ClockRollbackDetected ? "ClockRollbackDetected" : "TrustedTimeUnavailable",
                    PermissionGrantId = selected.GrantId,
                    PermissionExpiresAtUtc = selected.ExpiresAtUtc,
                    PermissionSource = selected.Source
                };
            }

            return new PermissionDecision
            {
                ActionKey = actionKey,
                IsAllowed = selected.Allowed,
                ReasonCode = IsEmergencyDeny(selected)
                    ? "EmergencyDeny"
                    : isTemporary
                        ? "TemporaryPermissionActive"
                        : "PermissionGrantMatched",
                PermissionGrantId = selected.GrantId,
                PermissionExpiresAtUtc = selected.ExpiresAtUtc,
                PermissionSource = selected.Source
            };
        }

        // Public files never need a grant at all - but only once no grant (including an
        // EmergencyDeny or an explicit deny for this exact file/tier) already decided the case
        // above. An explicit administrative decision always outranks this generic, content-aware
        // default; this only fills in when nothing more specific applies.
        if (fileHash is not null && fileRank == ClassificationTiers.RankOf(ClassificationTiers.Public))
        {
            return new PermissionDecision
            {
                ActionKey = actionKey,
                IsAllowed = true,
                ReasonCode = "PublicClassification",
                PermissionSource = PermissionSources.GlobalDefault
            };
        }

        var allowed = policy.Permissions.DefaultPermissions.TryGetValue(actionKey, out var configured)
            && configured;

        return new PermissionDecision
        {
            ActionKey = actionKey,
            IsAllowed = allowed,
            ReasonCode = allowed ? "GlobalDefaultAllow" : "GlobalDefaultDeny",
            PermissionSource = PermissionSources.GlobalDefault
        };
    }

    private static bool IsActive(PermissionGrant grant, DateTimeOffset nowUtc)
    {
        if (grant.RevokedAtUtc is not null) return false;
        if (grant.StartsAtUtc > nowUtc) return false;
        if (grant.ExpiresAtUtc is not null && grant.ExpiresAtUtc <= nowUtc) return false;
        return true;
    }

    // Action-level grant (no FileHash/ClassificationTier) always matches, regardless of whether
    // this evaluation is about a specific file - this is the existing, unchanged behavior for
    // every action key other than browser.upload/browser.drag-drop, and remains the broadest
    // fallback grant type for those two as well. A file/tier-scoped grant only ever applies when
    // this evaluation is actually about a specific file (fileHash provided) - it must never leak
    // into evaluating some unrelated action or a different file.
    private static bool MatchesFileScope(PermissionGrant grant, string? fileHash, int? fileRank)
    {
        if (grant.FileHash is null && grant.ClassificationTier is null) return true;
        if (fileHash is null) return false;

        if (grant.FileHash is not null)
            return grant.FileHash.Equals(fileHash, StringComparison.OrdinalIgnoreCase);

        // ClassificationTier-scoped: covers this file if the file's own rank is at or below the
        // granted tier's rank (a "Secret" grant also covers Public/Internal files, not the reverse).
        return fileRank is not null && fileRank.Value <= ClassificationTiers.RankOf(grant.ClassificationTier!);
    }

    private static int FileScopeSpecificity(PermissionGrant grant, string? fileHash)
    {
        if (fileHash is null) return 0;
        if (grant.FileHash is not null) return 2;
        if (grant.ClassificationTier is not null) return 1;
        return 0;
    }

    private static bool IsEmergencyDeny(PermissionGrant grant) =>
        !grant.Allowed && grant.Source.Equals(PermissionSources.EmergencyDeny, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesSubject(PermissionGrant grant, ClientContext context, AgentIdentity identity)
    {
        var expected = grant.SubjectId?.Trim() ?? "";
        return grant.SubjectType switch
        {
            PermissionSubjectTypes.Global => expected is "" or "*",
            PermissionSubjectTypes.UserSid => expected.Equals(context.UserSid, StringComparison.OrdinalIgnoreCase),
            PermissionSubjectTypes.Username => expected.Equals(context.Username, StringComparison.OrdinalIgnoreCase),
            PermissionSubjectTypes.DeviceId => Guid.TryParse(expected, out var id) && id == identity.DeviceId,
            PermissionSubjectTypes.MachineName => expected.Equals(identity.MachineName, StringComparison.OrdinalIgnoreCase)
                || expected.Equals(context.MachineName, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static int SubjectSpecificity(string subjectType) => subjectType switch
    {
        PermissionSubjectTypes.UserSid => 50,
        PermissionSubjectTypes.Username => 40,
        PermissionSubjectTypes.DeviceId => 30,
        PermissionSubjectTypes.MachineName => 20,
        PermissionSubjectTypes.Group => 10,
        PermissionSubjectTypes.Department => 5,
        _ => 0
    };
}
