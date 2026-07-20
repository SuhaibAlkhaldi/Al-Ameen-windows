using System.Text.RegularExpressions;
using CompanyDlp.Contracts;

namespace CompanyDlp.Core;

public sealed class ContentClassifier(
    PolicyStore policyStore,
    ContentNormalizer normalizer,
    FragmentSessionTracker fragmentTracker)
{
    private static readonly Regex EmailRegex = new(@"\b[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    public ClassificationResult Classify(ClassificationRequest request)
    {
        var result = new ClassificationResult();
        if (string.IsNullOrWhiteSpace(request.Text)) return result;

        foreach (var rule in policyStore.Get().SensitiveRules.Where(rule => rule.Enabled))
        {
            var matched = MatchRule(rule, request.Text);
            if (!matched) continue;

            result.Matches.Add(new ClassificationMatch
            {
                RuleId = rule.Id,
                RuleName = rule.Name,
                MatchType = rule.Type,
                MaskedPreview = Mask(request.Text)
            });
        }

        if (result.Matches.Count == 0 && request.TrackFragments)
        {
            var fragmentRule = fragmentTracker.AddAndDetect(request.SessionKey, request.Text);
            if (fragmentRule is not null)
            {
                result.FragmentAssemblyDetected = true;
                result.Matches.Add(new ClassificationMatch
                {
                    RuleId = fragmentRule.Id,
                    RuleName = fragmentRule.Name,
                    MatchType = "FragmentAssembly",
                    MaskedPreview = "fragmented-sensitive-value"
                });
            }
        }

        result.IsSensitive = result.Matches.Count > 0;
        return result;
    }

    private bool MatchRule(SensitiveRule rule, string text)
    {
        var comparison = rule.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        if (rule.Type.Equals(SensitiveRuleTypes.Keyword, StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(rule.Value) && text.Contains(rule.Value, comparison);
        }

        if (rule.Type.Equals(SensitiveRuleTypes.ExactValue, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(rule.Value)) return false;
            if (!rule.Normalize) return text.Contains(rule.Value, comparison);

            var candidate = normalizer.Normalize(text);
            var target = normalizer.Normalize(rule.Value);
            if (target.Length == 0 || candidate.Length == 0) return false;
            if (candidate.Contains(target, StringComparison.OrdinalIgnoreCase)) return true;

            var minimumFragmentLength = Math.Max(2, rule.MinimumBlockedFragmentLength);
            return rule.BlockIndividualFragments
                && candidate.Length >= minimumFragmentLength
                && candidate.Length < target.Length
                && target.Contains(candidate, StringComparison.OrdinalIgnoreCase);
        }

        if (rule.Type.Equals(SensitiveRuleTypes.Regex, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(rule.Pattern)) return false;
            try
            {
                var options = RegexOptions.CultureInvariant | (rule.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
                return Regex.IsMatch(text, rule.Pattern, options, TimeSpan.FromMilliseconds(250));
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        return rule.Type.Equals(SensitiveRuleTypes.AnyEmail, StringComparison.OrdinalIgnoreCase) && EmailRegex.IsMatch(text);
    }

    private static string Mask(string value)
    {
        var compact = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (compact.Length <= 8) return "********";
        return $"{compact[..4]}…{compact[^4..]}";
    }
}
