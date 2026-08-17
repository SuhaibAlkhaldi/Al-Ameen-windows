using System.Text.RegularExpressions;
using CompanyDlp.Contracts;

namespace CompanyDlp.Core;

// Pure filename logic for the "show classification in the file name itself" feature
// (FileInventoryScanner calls this once a file has a confirmed Up-to-Date classification).
// Chosen over an Explorer icon overlay because Windows caps overlay handlers at a small,
// system-wide number of slots shared with every other installed product (OneDrive, antivirus,
// etc.) - a real risk the badge silently never renders. A name prefix has no such limit and,
// unlike an overlay, survives the file leaving this machine entirely (email, upload, another
// device), which is the actual point of a DLP classification staying visible.
//
// Prefixed, not suffixed: several real-world surfaces (email clients, narrow list columns,
// older upload dialogs) truncate a long file name from the END when there isn't room to show
// it in full. A suffix tag placed right before the extension is exactly what gets cut off in
// that case; a prefix is always the first thing rendered regardless of how much of the rest
// gets truncated.
public static class FilenameClassificationTagger
{
    private static readonly Dictionary<string, string> TagsByTier = new(StringComparer.OrdinalIgnoreCase)
    {
        [ClassificationTiers.Public] = "[Public]",
        [ClassificationTiers.Internal] = "[Internal]",
        [ClassificationTiers.Secret] = "[Secret]",
        [ClassificationTiers.VerySecret] = "[Very Secret]",
    };

    // Matches a tag this class itself would have written, at the very start of the name only -
    // so a user's own file that happens to start with a literal "[Internal]" they typed themselves
    // is left alone on every scan after the one time we'd already have tagged it identically anyway.
    private static readonly Regex ExistingTagPrefix =
        new(@"^\[(Public|Internal|Secret|Very Secret)\]\s+", RegexOptions.Compiled);

    // fileName is the name only (Path.GetFileName), never a full path. Returns the file name the
    // file SHOULD have for the given tier - identical to fileName (no rename needed) only if it's
    // already correctly tagged, or the tier is unrecognized (falls back to untagged rather than
    // guessing).
    public static string BuildTaggedFileName(string fileName, string classificationTier)
    {
        var untagged = ExistingTagPrefix.Replace(fileName, "");
        return TagsByTier.TryGetValue(classificationTier, out var tag) ? tag + " " + untagged : untagged;
    }
}
