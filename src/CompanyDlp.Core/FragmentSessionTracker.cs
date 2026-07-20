using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using CompanyDlp.Contracts;

namespace CompanyDlp.Core;

public sealed class FragmentSessionTracker(ContentNormalizer normalizer, PolicyStore policyStore)
{
    private static readonly Regex EmailRegex = new(@"\b[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
    private readonly ConcurrentDictionary<string, FragmentBuffer> _buffers = new(StringComparer.OrdinalIgnoreCase);

    public SensitiveRule? AddAndDetect(string sessionKey, string text)
    {
        var policy = policyStore.Get();
        var normalized = normalizer.Normalize(text);
        var compactRaw = Regex.Replace(text ?? string.Empty, @"\s+", string.Empty);
        if (normalized.Length == 0 && compactRaw.Length == 0) return null;

        var buffer = _buffers.GetOrAdd(sessionKey, _ => new FragmentBuffer());
        lock (buffer.Sync)
        {
            var cutoff = DateTimeOffset.UtcNow.AddSeconds(-Math.Max(10, policy.Clipboard.FragmentWindowSeconds));
            buffer.Items.RemoveAll(item => item.At < cutoff);
            buffer.Items.Add(new FragmentEntry(DateTimeOffset.UtcNow, normalized, compactRaw));

            while (buffer.Items.Count > Math.Max(2, policy.Clipboard.MaxFragments))
            {
                buffer.Items.RemoveAt(0);
            }

            var exactRules = policy.SensitiveRules.Where(rule =>
                rule.Enabled && rule.DetectFragments &&
                rule.Type.Equals(SensitiveRuleTypes.ExactValue, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(rule.Value));

            foreach (var rule in exactRules)
            {
                var target = rule.Normalize ? normalizer.Normalize(rule.Value) : rule.Value;
                if (target.Length < 4) continue;

                for (var start = 0; start < buffer.Items.Count; start++)
                {
                    var combined = string.Empty;
                    for (var index = start; index < buffer.Items.Count; index++)
                    {
                        combined += buffer.Items[index].Normalized;
                        if (combined.Contains(target, StringComparison.OrdinalIgnoreCase)) return rule;
                        if (combined.Length > target.Length * 2) break;
                    }
                }
            }

            var anyEmailRule = policy.SensitiveRules.FirstOrDefault(rule =>
                rule.Enabled && rule.DetectFragments &&
                rule.Type.Equals(SensitiveRuleTypes.AnyEmail, StringComparison.OrdinalIgnoreCase));

            if (anyEmailRule is not null)
            {
                for (var start = 0; start < buffer.Items.Count; start++)
                {
                    var combinedRaw = string.Empty;
                    for (var index = start; index < buffer.Items.Count; index++)
                    {
                        combinedRaw += buffer.Items[index].CompactRaw;
                        if (EmailRegex.IsMatch(combinedRaw)) return anyEmailRule;
                        if (combinedRaw.Length > 320) break;
                    }
                }
            }
        }

        return null;
    }

    private sealed class FragmentBuffer
    {
        public object Sync { get; } = new();
        public List<FragmentEntry> Items { get; } = [];
    }

    private sealed record FragmentEntry(DateTimeOffset At, string Normalized, string CompactRaw);
}
