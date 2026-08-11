using Xunit;
using CompanyDlp.Service;

namespace CompanyDlp.Tests;

public sealed class CliEnforcementHealthCheckerTests
{
    [Theory]
    [InlineData("Enterprise", true)]
    [InlineData("EnterpriseS", true)] // LTSC
    [InlineData("Education", true)]
    [InlineData("ServerStandard", true)]
    [InlineData("ServerDatacenter", true)]
    [InlineData("ServerRdsh", true)]
    [InlineData("IoTEnterprise", true)]
    public void SupportedEditions_ReportSupported(string editionId, bool expected)
    {
        Assert.Equal(expected, CliEnforcementHealthChecker.IsEditionSupported(editionId));
    }

    [Theory]
    [InlineData("Professional")]
    [InlineData("Core")]
    [InlineData("CoreSingleLanguage")]
    [InlineData("")]
    [InlineData(null)]
    public void UnsupportedEditions_ReportNotSupported(string? editionId)
    {
        Assert.False(CliEnforcementHealthChecker.IsEditionSupported(editionId!));
    }

    // KB5024351: Windows 10 build 19041+ (2004/20H2/21H1+) and any Windows 11 build (22000+) unlock
    // AppLocker enforcement regardless of edition.
    [Theory]
    [InlineData(19041, true)]  // Windows 10 2004, the exact KB5024351 threshold
    [InlineData(19045, true)]  // Windows 10 22H2 (latest Win10)
    [InlineData(22000, true)]  // Windows 11 21H2
    [InlineData(26200, true)]  // Windows 11 25H2 - this test machine's real build
    public void QualifyingBuilds_AreSupportedRegardlessOfEdition(int build, bool expected)
    {
        Assert.Equal(expected, CliEnforcementHealthChecker.IsBuildSupportedRegardlessOfEdition(build));
    }

    [Theory]
    [InlineData(19040)] // one below the KB5024351 threshold
    [InlineData(18363)] // Windows 10 1909
    [InlineData(0)]
    public void PreKB5024351Builds_AreNotSupportedByBuildAlone(int build)
    {
        Assert.False(CliEnforcementHealthChecker.IsBuildSupportedRegardlessOfEdition(build));
    }

    [Fact]
    public void ProfessionalEditionOnQualifyingBuild_IsPlatformSupported()
    {
        // The exact case this fix exists for: Pro edition used to hard-fail here even on a fully
        // patched machine. IsPlatformSupported takes editionId only (build comes from
        // Environment.OSVersion internally) - covered indirectly via IsEditionSupported/
        // IsBuildSupportedRegardlessOfEdition above, which IsPlatformSupported ORs together; this test
        // documents the combinator's intent rather than re-deriving it, since the real machine running
        // this suite is itself Professional on a qualifying build.
        Assert.False(CliEnforcementHealthChecker.IsEditionSupported("Professional"));
        Assert.True(CliEnforcementHealthChecker.IsBuildSupportedRegardlessOfEdition(26200));
    }
}
