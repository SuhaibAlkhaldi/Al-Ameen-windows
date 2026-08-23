using System.Text.RegularExpressions;

namespace CompanyDlp.Core;

// Deterministic, regex/checksum-based corrections layered on top of LocalEntityExtractor's GLiNER
// output - for entity shapes (emails, digit-sequence IDs) that have a known, precise structure and
// don't actually need a neural model's judgment call. Kept separate from LocalEntityExtractor.cs so
// each piece here is independently unit-testable without constructing the ONNX session.
public static class StructuredEntityValidator
{
    // Practical email pattern (not the full RFC 5322 grammar, which permits quoted strings and
    // comments that essentially never appear in real documents and would only invite false
    // positives here): local part is dot-separated segments of common unquoted atext characters,
    // domain is dot-separated DNS labels. Matches the actual shape of every real-world email this
    // is meant to catch.
    public static readonly Regex EmailPattern = new(
        @"[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+(?:\.[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+)*@[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)+",
        RegexOptions.Compiled);

    // Finds every full email address in raw text with its character offsets - independent of, and
    // deliberately not derived from, GLiNER's word/span decode. GLiNER's fine-tuned checkpoint
    // reliably splits "name@domain.com" into a PERSON-tagged local part plus assorted low-confidence
    // fragments instead of one clean EMAIL span (confirmed identically against the original PyTorch
    // model, not an artifact of the ONNX export or the C# port - see AiModel/README.md's write-up of
    // that investigation) - a plain regex is strictly more reliable for this one specific shape.
    public static IReadOnlyList<(string Value, int Start, int End)> FindEmails(string text)
    {
        if (string.IsNullOrEmpty(text)) return [];

        var matches = new List<(string Value, int Start, int End)>();
        foreach (Match match in EmailPattern.Matches(text))
        {
            matches.Add((match.Value, match.Index, match.Index + match.Length));
        }
        return matches;
    }

    public static bool RangesOverlap(int start1, int end1, int start2, int end2) =>
        !(start1 >= end2 || start2 >= end1);

    // Luhn checksum, implemented from scratch: starting from the rightmost digit, double every
    // second digit; if doubling produces a two-digit result, subtract 9 (equivalent to summing its
    // own two digits); sum everything; valid iff the total is a multiple of 10.
    public static bool PassesLuhnCheck(string digitsOnly)
    {
        if (string.IsNullOrEmpty(digitsOnly)) return false;

        var sum = 0;
        var alternate = false;
        for (var i = digitsOnly.Length - 1; i >= 0; i--)
        {
            if (!char.IsAsciiDigit(digitsOnly[i])) return false;

            var digit = digitsOnly[i] - '0';
            if (alternate)
            {
                digit *= 2;
                if (digit > 9) digit -= 9;
            }
            sum += digit;
            alternate = !alternate;
        }
        return sum % 10 == 0;
    }

    // Reclassifies an entity the model already tagged CREDIT_CARD or NATIONAL_ID - never called for
    // any other type, and never invents a new detection. GLiNER's fine-tuned checkpoint confuses
    // these two labels for a bare digit sequence with weak surrounding context (confirmed on the
    // original PyTorch model too - a model-quality characteristic, not an export or C#-port issue).
    // Luhn is a real, deterministic property genuine credit card numbers have and real national ID
    // numbers essentially never happen to also satisfy, so it settles the two far more reliably than
    // the model's own label choice for this specific pair.
    public static string ReclassifyDigitEntityType(string value)
    {
        var digitsOnly = new string(value.Where(char.IsAsciiDigit).ToArray());
        var isCreditCard = digitsOnly.Length is >= 13 and <= 19 && PassesLuhnCheck(digitsOnly);
        return isCreditCard ? "CREDIT_CARD" : "NATIONAL_ID";
    }
}
