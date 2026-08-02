namespace CompanyDlp.Contracts;

// Extracted from CompanyDlp.Desktop.Protection.ClipboardProtectionManager.InspectClipboardAsync purely
// so this decision is directly unit-testable - ClipboardProtectionManager itself is deeply WPF-coupled
// (real System.Windows.Clipboard, HwndSource, DispatcherTimer, a live Window), none of which is
// available or appropriate to construct in CompanyDlp.Tests. This one boolean gate is the entire
// "should we block this clipboard copy" decision the class makes once it already has a
// ClassificationResult back from the DLP service; behavior is unchanged from the original inline
// checks. See ClassificationResult.AllowedByGrant's own comment for why that field exists - this is
// the one place that field is actually acted on, not just recorded for audit purposes.
public static class ClipboardBlockDecision
{
    public static bool ShouldBlock(bool blockSensitiveTextPolicy, bool isSensitive, bool allowedByGrant)
    {
        if (!blockSensitiveTextPolicy) return false;
        if (!isSensitive) return false;
        if (allowedByGrant) return false;
        return true;
    }
}
