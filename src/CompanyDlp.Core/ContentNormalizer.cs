using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CompanyDlp.Core;

public sealed class ContentNormalizer
{
    private static readonly Regex AtAlias = new(@"(?:\[at\]|\(at\)|\{at\}|\s+at\s+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex DotAlias = new(@"(?:\[dot\]|\(dot\)|\{dot\}|\s+dot\s+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var text = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        text = AtAlias.Replace(text, "@");
        text = DotAlias.Replace(text, ".");

        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (char.IsLetterOrDigit(character) || category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }
}
