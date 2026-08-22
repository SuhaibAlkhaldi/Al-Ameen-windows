using CompanyDlp.AdminApi.Services;
using CompanyDlp.Contracts;
using CompanyDlp.Service;
using Xunit;

namespace CompanyDlp.Tests;

// Covers the 2026-08-22 fix for "every browser extension got blocked, including Ameen's own, with the
// browser force-install/block-others policy machinery": a real deployment shipped literal
// "REPLACE_AFTER_PUBLISHING_EXTENSION" placeholders with BlockUnapprovedExtensions enabled - the
// forcelist entry Chrome/Edge actually validated was garbage and got silently discarded, while the
// separate, unconditional ExtensionInstallBlocklist="*" write went through regardless. These tests
// exercise the pure decision logic (ExtensionPolicyValidator, BrowserPolicyManager.BuildWritePlan) with
// no registry access at all - this session had no elevated/administrator rights to write real HKLM
// policy keys (confirmed by attempting it), so live registry behavior could not be verified here; see
// BrowserPolicyManager.ExtensionPolicyValueName's comment for what that decision was based on instead.
public sealed class ExtensionPolicyTests
{
    private const string ValidChromeId = "abcdefghijklmnopabcdefghijklmnop"; // 32 chars, a-p only
    private const string ValidUpdateUrl = "https://dlp-cdn.example.com/extensions/update.xml";
    private const string ValidFirefoxId = "company-dlp@company.local";
    private const string ValidFirefoxXpiUrl = "https://dlp-cdn.example.com/extensions/company-dlp.xpi";

    [Theory]
    [InlineData("REPLACE_AFTER_PUBLISHING_EXTENSION", ValidUpdateUrl)]
    [InlineData(ValidChromeId, "REPLACE_WITH_CHROME_STORE_OR_SELF_HOSTED_UPDATE_URL")]
    [InlineData("replace_lowercase_variant", ValidUpdateUrl)]
    public void Validate_PlaceholderValue_IsInvalid(string extensionId, string updateUrl)
    {
        var status = ExtensionPolicyValidator.Validate(extensionId, updateUrl, ExtensionPlatform.Chrome);
        Assert.Equal(ExtensionForceInstallStatus.Invalid, status);
    }

    [Fact]
    public void Validate_BothBlank_IsNotConfigured()
    {
        var status = ExtensionPolicyValidator.Validate("", "", ExtensionPlatform.Chrome);
        Assert.Equal(ExtensionForceInstallStatus.NotConfigured, status);
    }

    [Fact]
    public void Validate_OnlyIdSet_IsInvalid()
    {
        // A partial config (one field set, the other blank) is a real misconfiguration, not
        // "unconfigured" - it must not be silently treated as NotConfigured.
        var status = ExtensionPolicyValidator.Validate(ValidChromeId, "", ExtensionPlatform.Chrome);
        Assert.Equal(ExtensionForceInstallStatus.Invalid, status);
    }

    [Fact]
    public void Validate_MalformedChromeId_IsInvalid()
    {
        // Chrome/Edge extension ids are exactly 32 chars, each one a-p only.
        var status = ExtensionPolicyValidator.Validate("too-short-id", ValidUpdateUrl, ExtensionPlatform.Chrome);
        Assert.Equal(ExtensionForceInstallStatus.Invalid, status);
    }

    [Fact]
    public void Validate_NonHttpsUpdateUrl_IsInvalid()
    {
        var status = ExtensionPolicyValidator.Validate(ValidChromeId, "http://insecure.example.com/update.xml", ExtensionPlatform.Chrome);
        Assert.Equal(ExtensionForceInstallStatus.Invalid, status);
    }

    [Theory]
    [InlineData(ExtensionPlatform.Chrome)]
    [InlineData(ExtensionPlatform.Edge)]
    public void Validate_WellFormedChromiumIdAndHttpsUrl_IsValid(ExtensionPlatform platform)
    {
        var status = ExtensionPolicyValidator.Validate(ValidChromeId, ValidUpdateUrl, platform);
        Assert.Equal(ExtensionForceInstallStatus.Valid, status);
    }

    [Fact]
    public void Validate_WellFormedFirefoxIdAndUrl_IsValid()
    {
        var status = ExtensionPolicyValidator.Validate(ValidFirefoxId, ValidFirefoxXpiUrl, ExtensionPlatform.Firefox);
        Assert.Equal(ExtensionForceInstallStatus.Valid, status);
    }

    [Fact]
    public void Validate_FirefoxIdRejectedAsChromeId_IsInvalid()
    {
        // company-dlp@company.local is not a valid Chrome/Edge id (must be 32 a-p chars) - the
        // platform parameter must actually change which format is accepted.
        var status = ExtensionPolicyValidator.Validate(ValidFirefoxId, ValidUpdateUrl, ExtensionPlatform.Chrome);
        Assert.Equal(ExtensionForceInstallStatus.Invalid, status);
    }

    [Fact]
    public void BuildWritePlan_ValidStatus_WritesForcelistAndRequestedBlocklist()
    {
        var plan = BrowserPolicyManager.BuildWritePlan(ExtensionForceInstallStatus.Valid, blockOthersRequested: true);
        Assert.True(plan.WriteForcelist);
        Assert.True(plan.WriteBlocklist);
    }

    [Fact]
    public void BuildWritePlan_ValidStatus_BlockOthersNotRequested_DoesNotWriteBlocklist()
    {
        var plan = BrowserPolicyManager.BuildWritePlan(ExtensionForceInstallStatus.Valid, blockOthersRequested: false);
        Assert.True(plan.WriteForcelist);
        Assert.False(plan.WriteBlocklist);
    }

    [Theory]
    [InlineData(ExtensionForceInstallStatus.Invalid)]
    [InlineData(ExtensionForceInstallStatus.NotConfigured)]
    public void BuildWritePlan_NotValidStatus_NeverWritesBlocklistEvenIfRequested(ExtensionForceInstallStatus status)
    {
        // The core fix: BlockUnapprovedExtensions=true must never translate into
        // ExtensionInstallBlocklist="*" unless the forcelist entry backing it is actually valid -
        // otherwise every extension (including Ameen's own) gets blocked with nothing installed to
        // replace them.
        var plan = BrowserPolicyManager.BuildWritePlan(status, blockOthersRequested: true);
        Assert.False(plan.WriteForcelist);
        Assert.False(plan.WriteBlocklist);
    }

    [Fact]
    public void TenantPolicySanitizer_PlaceholderChromeExtensionId_IsRejected()
    {
        var policy = new DlpPolicy
        {
            Browser = new BrowserPolicy
            {
                ChromeExtensionId = "REPLACE_AFTER_PUBLISHING_EXTENSION",
                ChromeExtensionUpdateUrl = "REPLACE_WITH_CHROME_STORE_OR_SELF_HOSTED_UPDATE_URL"
            }
        };

        var error = TenantPolicySanitizer.Normalize(policy);
        Assert.Equal("InvalidChromeExtensionForceInstall", error);
    }

    [Fact]
    public void TenantPolicySanitizer_UnconfiguredExtensions_PassesThrough()
    {
        // A tenant that hasn't set up self-hosted extension distribution yet must not be blocked from
        // saving any other policy change - NotConfigured is a legitimate, if unprotected, state.
        var policy = new DlpPolicy();
        var error = TenantPolicySanitizer.Normalize(policy);
        Assert.Null(error);
    }

    [Fact]
    public void TenantPolicySanitizer_WellFormedExtensions_PassesThrough()
    {
        var policy = new DlpPolicy
        {
            Browser = new BrowserPolicy
            {
                ChromeExtensionId = ValidChromeId,
                ChromeExtensionUpdateUrl = ValidUpdateUrl,
                EdgeExtensionId = ValidChromeId,
                EdgeExtensionUpdateUrl = ValidUpdateUrl,
                FirefoxExtensionId = ValidFirefoxId,
                FirefoxExtensionUpdateUrl = ValidFirefoxXpiUrl
            }
        };

        var error = TenantPolicySanitizer.Normalize(policy);
        Assert.Null(error);
    }

    [Fact]
    public void ExtensionPolicyValueName_IsSequentialIndexOne()
    {
        // Chrome/Edge only recognize a registry subkey as a JSON list when its value names are
        // exactly "1".."N" (RegistryDict::ToValue() in Chromium's policy_loader_win.cc) - any other
        // naming (including the old fixed "9999") silently fails ExtensionInstallForcelist's list-typed
        // schema validation. See the constant's own comment for why this could not be confirmed live
        // against a real browser in this change.
        Assert.Equal("1", BrowserPolicyManager.ExtensionPolicyValueName);
    }
}
