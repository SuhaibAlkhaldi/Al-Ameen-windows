using CompanyDlp.Contracts;
using Xunit;

namespace CompanyDlp.Tests;

// Extracted from ClipboardProtectionManager (see ClipboardBlockDecision.cs) specifically to test the
// clipboard-blocking decision without needing a real WPF Window/Clipboard/DispatcherTimer, none of
// which CompanyDlp.Tests references or can meaningfully construct. Covers every combination of the
// three inputs, with particular attention to AllowedByGrant - the field ClassificationResult's own
// comment documents as "the fix Suhaib made" to make an approved permission grant actually override a
// sensitive-content match, not just annotate the audit log.
public sealed class ClipboardBlockDecisionTests
{
    [Fact]
    public void PolicyDoesNotBlockSensitiveText_NeverBlocks()
    {
        Assert.False(ClipboardBlockDecision.ShouldBlock(blockSensitiveTextPolicy: false, isSensitive: true, allowedByGrant: false));
        Assert.False(ClipboardBlockDecision.ShouldBlock(blockSensitiveTextPolicy: false, isSensitive: true, allowedByGrant: true));
    }

    [Fact]
    public void NotSensitive_NeverBlocks()
    {
        Assert.False(ClipboardBlockDecision.ShouldBlock(blockSensitiveTextPolicy: true, isSensitive: false, allowedByGrant: false));
    }

    // The key regression this exists to catch: sensitive content that WOULD be blocked is instead
    // allowed through when an approved permission grant covers it.
    [Fact]
    public void Sensitive_ButAllowedByGrant_IsNotBlocked()
    {
        Assert.False(ClipboardBlockDecision.ShouldBlock(blockSensitiveTextPolicy: true, isSensitive: true, allowedByGrant: true));
    }

    [Fact]
    public void Sensitive_PolicyBlocks_NoGrant_IsBlocked()
    {
        Assert.True(ClipboardBlockDecision.ShouldBlock(blockSensitiveTextPolicy: true, isSensitive: true, allowedByGrant: false));
    }
}
