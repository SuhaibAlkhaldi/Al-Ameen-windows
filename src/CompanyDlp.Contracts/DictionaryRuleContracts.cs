namespace CompanyDlp.Contracts;

// Mirrors the Python AI DLP system's DictionaryRuleItem shape exactly (entities + condition +
// severity, plus an optional text_keywords list) - see DictionaryEvaluator (Core) for the matching
// evaluation logic, a direct port of that system's evaluator.py.
public sealed class DictionaryRuleItem
{
    public List<string> Entities { get; set; } = [];
    public string Condition { get; set; } = "OR"; // "AND" | "OR"
    public string Severity { get; set; } = ClassificationTiers.Public;
    public List<string> TextKeywords { get; set; } = [];
}

public sealed class DictionaryRulesResponse
{
    public long Version { get; set; }
    public List<DictionaryRuleItem> Rules { get; set; } = [];
}
