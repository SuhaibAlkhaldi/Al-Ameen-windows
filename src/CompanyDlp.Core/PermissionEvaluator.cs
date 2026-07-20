using CompanyDlp.Contracts;

namespace CompanyDlp.Core;

public sealed class PermissionEvaluator(ITrustedClock? trustedClock = null)
{
    public PermissionDecision Evaluate(
        DlpPolicy policy,
        string actionKey,
        ClientContext context,
        AgentIdentity identity,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionKey);
        context ??= new ClientContext();
        var clock = trustedClock?.GetSnapshot();
        var evaluationTimeUtc = clock?.UtcNow ?? nowUtc;

        var candidates = policy.Permissions.Grants
            .Where(grant => grant.ActionKey.Equals(actionKey, StringComparison.OrdinalIgnoreCase))
            .Where(grant => grant.RevokedAtUtc is null)
            .Where(grant => grant.StartsAtUtc <= evaluationTimeUtc)
            .Where(grant => grant.ExpiresAtUtc is null || grant.ExpiresAtUtc > evaluationTimeUtc)
            .Where(grant => MatchesSubject(grant, context, identity))
            .OrderByDescending(grant => IsEmergencyDeny(grant))
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
                    : selected.Source.Equals(PermissionSources.TemporaryGrant, StringComparison.OrdinalIgnoreCase)
                        ? "TemporaryPermissionActive"
                        : "PermissionGrantMatched",
                PermissionGrantId = selected.GrantId,
                PermissionExpiresAtUtc = selected.ExpiresAtUtc,
                PermissionSource = selected.Source
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
