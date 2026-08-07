using CompanyDlp.Contracts;

namespace CompanyDlp.Core;

// An entity detected in a document (type e.g. "EMAIL"/"PHONE"/"PASSPORT", plus the matched text).
public sealed record DetectedEntity(string Type, string Value);

public sealed record DictionaryEvaluationResult(string Severity, string Reasoning);

// Direct C# port of the AI DLP system's DictionaryEvaluator.evaluate() (Python) - same severity
// ranking, same AND/OR matching semantics, same fallback behavior when no rules are configured, and
// the same text_keywords matching (a rule can trigger on a literal keyword like "password" with no
// entity type at all). Kept logically identical to the Python original so admin-configured rules
// behave the same regardless of which side evaluates them.
public static class DictionaryEvaluator
{
    private static readonly Dictionary<string, int> SeverityRank = new(StringComparer.OrdinalIgnoreCase)
    {
        [ClassificationTiers.Public] = 0,
        [ClassificationTiers.Internal] = 1,
        [ClassificationTiers.Secret] = 2,
        [ClassificationTiers.VerySecret] = 3
    };

    public static DictionaryEvaluationResult Evaluate(
        IReadOnlyList<DetectedEntity> extractedEntities,
        IReadOnlyList<DictionaryRuleItem>? rules,
        string rawText)
    {
        const string defaultReasoning = "Defaulted to Public, no matching entities found.";

        if (rules is null || rules.Count == 0)
        {
            return EvaluateFallback(extractedEntities, rawText, defaultReasoning);
        }

        var lowerRawText = rawText?.ToLowerInvariant() ?? "";
        var entityTypes = new HashSet<string>(
            extractedEntities.Select(e => e.Type.ToUpperInvariant()),
            StringComparer.OrdinalIgnoreCase);
        var entityValues = extractedEntities.Select(e => e.Value.ToLowerInvariant()).ToList();

        var highestSeverity = ClassificationTiers.Public;
        var reasoning = defaultReasoning;

        foreach (var rule in rules)
        {
            var ruleEntities = rule.Entities;
            var textKeywords = rule.TextKeywords;
            var condition = rule.Condition;
            var ruleSeverity = rule.Severity;

            var entityTypeMatches = ruleEntities
                .Where(required => entityTypes.Contains(required.ToUpperInvariant()))
                .ToList();

            var textMatches = new List<string>();
            foreach (var keyword in textKeywords)
            {
                var keywordLower = keyword.ToLowerInvariant().Trim();
                if (keywordLower.Length == 0) continue;
                if (lowerRawText.Contains(keywordLower) || entityValues.Any(v => v.Contains(keywordLower)))
                {
                    textMatches.Add(keyword);
                }
            }

            bool match;
            var matchReason = "";
            if (condition.Equals("AND", StringComparison.OrdinalIgnoreCase))
            {
                var entityOk = entityTypeMatches.Count == ruleEntities.Count;
                var textOk = textMatches.Count == textKeywords.Count;
                match = entityOk && textOk && (ruleEntities.Count > 0 || textKeywords.Count > 0);
                if (match)
                {
                    matchReason = $"Matched ALL required entities [{string.Join(",", entityTypeMatches)}] AND keywords [{string.Join(",", textMatches)}]";
                }
            }
            else
            {
                match = entityTypeMatches.Count > 0 || textMatches.Count > 0;
                if (match)
                {
                    var found = entityTypeMatches.Concat(textMatches);
                    matchReason = $"Matched ANY of: {string.Join(",", found)}";
                }
            }

            if (!match) continue;

            var ruleRank = SeverityRank.GetValueOrDefault(ruleSeverity, 0);
            var currentRank = SeverityRank.GetValueOrDefault(highestSeverity, 0);
            if (ruleRank > currentRank)
            {
                highestSeverity = ruleSeverity;
                reasoning = $"Triggered rule '{ruleSeverity}': {matchReason}";
            }
        }

        return new DictionaryEvaluationResult(highestSeverity, reasoning);
    }

    // Same fallback the Python evaluator applies when no admin-configured rules exist yet: the
    // most sensitive entity types (PASSPORT/CREDIT_CARD/NATIONAL_ID) force Very_Secret, the
    // moderately sensitive ones (PHONE/EMAIL) force Internal, otherwise Public.
    private static DictionaryEvaluationResult EvaluateFallback(
        IReadOnlyList<DetectedEntity> extractedEntities,
        string rawText,
        string defaultReasoning)
    {
        if (extractedEntities.Count == 0 && string.IsNullOrEmpty(rawText))
        {
            return new DictionaryEvaluationResult(ClassificationTiers.Public, defaultReasoning);
        }

        var foundTypes = new HashSet<string>(
            extractedEntities.Select(e => e.Type.ToUpperInvariant()),
            StringComparer.OrdinalIgnoreCase);

        if (foundTypes.Overlaps(["PASSPORT", "CREDIT_CARD", "NATIONAL_ID"]))
        {
            return new DictionaryEvaluationResult(ClassificationTiers.VerySecret, "Fallback rule matched highly sensitive types.");
        }

        if (foundTypes.Overlaps(["PHONE", "EMAIL"]))
        {
            return new DictionaryEvaluationResult(ClassificationTiers.Internal, "Fallback rule matched internally sensitive types.");
        }

        return new DictionaryEvaluationResult(ClassificationTiers.Public, defaultReasoning);
    }
}
