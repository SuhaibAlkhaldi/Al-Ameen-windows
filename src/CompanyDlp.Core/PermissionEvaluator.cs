using CompanyDlp.Contracts;

namespace CompanyDlp.Core;

public sealed class PermissionEvaluator(FileClassificationCache? classificationCache = null, ITrustedClock? trustedClock = null)
{
    // Every other file/tier-aware action (browser.upload, file.decrypt, ...) auto-allows Public
    // content with no grant needed - printing is deliberately stricter by explicit user request:
    // every print, regardless of tier including Public, requires an explicit grant. Kept as a
    // small opt-out set rather than a policy flag since this is a fixed product decision for this
    // one action, not something meant to be admin-configurable per tenant.
    // FileWatermarkDisable joins FilePrint here by the same explicit product decision - a request
    // to hide the file watermark must always go through an approval, even for Public-tier content,
    // rather than silently auto-allowing Public the way most other actions do (see the fallback
    // this set opts out of, further down).
    private static readonly HashSet<string> ActionsRequiringGrantEvenForPublic =
        new(StringComparer.OrdinalIgnoreCase) { ActionKeys.FilePrint, ActionKeys.FileWatermarkDisable };

    // Every other tier-scoped grant (print included) uses "this tier and anything less sensitive"
    // semantics - see MatchesFileScope's comment. FileWatermarkDisable is deliberately different by
    // explicit product decision: an admin approving "hide the watermark on my Secret files" must
    // NOT silently also hide it on that employee's Internal/Public files - each tier is opted into
    // independently. Kept as its own opt-in set (mirroring ActionsRequiringGrantEvenForPublic above)
    // rather than a per-grant flag, since this is fixed behavior for this one action, not something
    // meant to vary per tenant/grant.
    private static readonly HashSet<string> ActionsRequiringExactTierMatch =
        new(StringComparer.OrdinalIgnoreCase) { ActionKeys.FileWatermarkDisable };

    public PermissionDecision Evaluate(
        DlpPolicy policy,
        string actionKey,
        ClientContext context,
        AgentIdentity identity,
        DateTimeOffset nowUtc,
        string? fileHash = null,
        string? knownClassificationTier = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionKey);
        context ??= new ClientContext();
        var clock = trustedClock?.GetSnapshot();
        var evaluationTimeUtc = clock?.UtcNow ?? nowUtc;

        // A file/tier-scoped grant only ever matters when this evaluation is actually about a
        // specific file. Two ways to establish that context: a fileHash (looked up in the
        // classification cache - a cache miss is treated as the most sensitive tier, fail-closed),
        // or a tier already known some other way (e.g. a print job resolved via a filename's
        // classification tag, with no real file hash available at all - see PrintProtectionMonitor).
        // knownClassificationTier always wins when both are somehow provided, since it represents a
        // more direct signal than a hash lookup that could itself be stale/wrong.
        var fileRank = knownClassificationTier is not null
            ? ClassificationTiers.RankOf(knownClassificationTier)
            : fileHash is null
                ? (int?)null
                : ClassificationTiers.RankOf(classificationCache?.TryGet(fileHash)?.Classification ?? ClassificationTiers.VerySecret);

        var candidates = policy.Permissions.Grants
            .Where(grant => grant.ActionKey.Equals(actionKey, StringComparison.OrdinalIgnoreCase))
            .Where(grant => MatchesSubject(grant, context, identity))
            .Where(grant => IsActive(grant, evaluationTimeUtc))
            .Where(grant => MatchesFileScope(grant, fileHash, fileRank, ActionsRequiringExactTierMatch.Contains(actionKey)))
            .OrderByDescending(grant => IsEmergencyDeny(grant))
            .ThenByDescending(grant => FileScopeSpecificity(grant, fileHash, fileRank, ActionsRequiringExactTierMatch.Contains(actionKey)))
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
        // default; this only fills in when nothing more specific applies. Skipped entirely for
        // actions in ActionsRequiringGrantEvenForPublic (see its comment).
        if (!ActionsRequiringGrantEvenForPublic.Contains(actionKey)
            && fileRank is not null && fileRank == ClassificationTiers.RankOf(ClassificationTiers.Public))
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

        // An action whose DefaultPermissions is already Deny (e.g. browser.upload) is unaffected by
        // this - it falls through to the same GlobalDefaultDeny below it always has. This only ever
        // matters for an action whose default is Allow (today, only file.decrypt): a file evaluated
        // WITH classification context (fileHash resolved - whether or not that hash has a cache hit,
        // a miss already fail-closed to VerySecret above) that ranks above Public and has no matching
        // grant must be denied despite the action's default, since an explicit grant (plain,
        // file-scoped, or tier-scoped) is required to access anything above Public once classification
        // is known. A file with NO classification context at all (fileHash is null - e.g. a legacy
        // .dlpenc file predating this feature, or the encrypt-time classification lookup failed to
        // record an association) is untouched by this and keeps the action's plain default, so
        // existing decrypt workflows keep working during the migration window.
        if (allowed && fileRank is not null && fileRank.Value > ClassificationTiers.RankOf(ClassificationTiers.Public))
        {
            return new PermissionDecision
            {
                ActionKey = actionKey,
                IsAllowed = false,
                ReasonCode = "ClassificationRequiresExplicitGrant",
                PermissionSource = PermissionSources.GlobalDefault
            };
        }

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
    // this evaluation actually has file context - either a real fileHash (exact-file grants) or at
    // least a known tier (tier-scoped grants only; an exact-file grant can never match without a
    // real hash to compare against, even if the tier happens to be known some other way) - it must
    // never leak into evaluating some unrelated action or a different file.
    private static bool MatchesFileScope(PermissionGrant grant, string? fileHash, int? fileRank, bool requireExactTierMatch = false)
    {
        if (grant.FileHash is null && grant.ClassificationTier is null) return true;
        if (fileHash is null && fileRank is null) return false;

        if (grant.FileHash is not null)
            return fileHash is not null && grant.FileHash.Equals(fileHash, StringComparison.OrdinalIgnoreCase);

        // ClassificationTier-scoped: by default covers this file if the file's own rank is at or
        // below the granted tier's rank (a "Secret" grant also covers Public/Internal files, not
        // the reverse) - this is print's behavior and the default for every other action. Actions in
        // ActionsRequiringExactTierMatch (see that field's comment) opt out of the "and below" widening
        // - a "Secret" grant for those actions covers Secret files only, never Internal/Public.
        return fileRank is not null && (requireExactTierMatch
            ? fileRank.Value == ClassificationTiers.RankOf(grant.ClassificationTier!)
            : fileRank.Value <= ClassificationTiers.RankOf(grant.ClassificationTier!));
    }

    private static int FileScopeSpecificity(PermissionGrant grant, string? fileHash, int? fileRank, bool requireExactTierMatch = false)
    {
        if (fileHash is null && fileRank is null) return 0;
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
