using CompanyDlp.Contracts;

namespace CompanyDlp.Core;

// Decides which file versions ship their extracted text to the backend, for the File Tracking diff viewer.
// Only the sensitive tiers (Restricted, Secret, Very Secret) are captured: the diff is the one place where a
// reviewer sees the text itself, so it is stored (encrypted at rest on the backend) only where that matters.
// Public files stay text-free. Protected (.dlpenc) files are never captured: their bytes are
// ciphertext and their plaintext must not leave the device through this path.
public static class FileVersionTextCapture
{
    // Above this, the version is recorded without text and the diff reports it as unavailable. This keeps one
    // change envelope from growing without bound. A very large document is still tracked; only its text is skipped.
    public const int MaxCapturedTextChars = 1_000_000;

    private static readonly HashSet<string> CapturedTiers = new(StringComparer.Ordinal)
    {
        ClassificationTiers.Internal,
        ClassificationTiers.Secret,
        ClassificationTiers.VerySecret
    };

    public static string? ForEnvelope(string? normalizedText, string? classificationTier, bool isProtected)
    {
        if (normalizedText is null || isProtected) return null;
        if (classificationTier is null || !CapturedTiers.Contains(classificationTier)) return null;
        if (normalizedText.Length > MaxCapturedTextChars) return null;

        return normalizedText;
    }
}
