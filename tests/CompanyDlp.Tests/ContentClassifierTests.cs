using CompanyDlp.Contracts;
using CompanyDlp.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CompanyDlp.Tests;

public sealed class ContentClassifierTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "CompanyDlpTests", Guid.NewGuid().ToString("N"));
    private readonly string? _oldPolicyPath = Environment.GetEnvironmentVariable("COMPANY_DLP_POLICY_PATH");

    [Fact]
    public void KeywordInsideSentence_IsBlocked()
    {
        var classifier = CreateClassifier();
        var result = classifier.Classify(new ClassificationRequest { Text = "This customer record is confidential." });
        Assert.True(result.IsSensitive);
        Assert.Contains(result.Matches, match => match.RuleId == "keyword");
    }

    [Theory]
    [InlineData("finance@company.com")]
    [InlineData("user@yahoo.co.uk")]
    [InlineData("name+tag@custom-domain.dev")]
    public void AnyCompleteEmail_IsBlocked(string email)
    {
        var classifier = CreateClassifier();
        var result = classifier.Classify(new ClassificationRequest { Text = email });
        Assert.True(result.IsSensitive);
        Assert.Contains(result.Matches, match => match.RuleId == "any-email");
    }

    [Fact]
    public void AnyEmailCopiedWithSeparatorsAsFragments_IsDetected()
    {
        var classifier = CreateClassifier();
        var session = "employee-1";
        Assert.False(classifier.Classify(new ClassificationRequest { Text = "finance", SessionKey = session, TrackFragments = true }).IsSensitive);
        Assert.False(classifier.Classify(new ClassificationRequest { Text = "@", SessionKey = session, TrackFragments = true }).IsSensitive);
        Assert.False(classifier.Classify(new ClassificationRequest { Text = "company", SessionKey = session, TrackFragments = true }).IsSensitive);
        Assert.False(classifier.Classify(new ClassificationRequest { Text = ".", SessionKey = session, TrackFragments = true }).IsSensitive);
        var final = classifier.Classify(new ClassificationRequest { Text = "com", SessionKey = session, TrackFragments = true });
        Assert.True(final.IsSensitive);
        Assert.True(final.FragmentAssemblyDetected);
    }

    [Fact]
    public void NormalText_IsAllowed()
    {
        var classifier = CreateClassifier();
        var result = classifier.Classify(new ClassificationRequest { Text = "The meeting starts tomorrow at ten." });
        Assert.False(result.IsSensitive);
    }

    private ContentClassifier CreateClassifier()
    {
        Directory.CreateDirectory(_tempDirectory);
        var path = Path.Combine(_tempDirectory, "policy.json");
        Environment.SetEnvironmentVariable("COMPANY_DLP_POLICY_PATH", path);
        var store = new PolicyStore(new MachineDataProtector(), NullLogger<PolicyStore>.Instance);
        store.Save(new DlpPolicy
        {
            SensitiveRules =
            [
                new SensitiveRule { Id = "keyword", Name = "Keyword", Type = SensitiveRuleTypes.Keyword, Value = "confidential", DetectFragments = false },
                new SensitiveRule { Id = "any-email", Name = "Any email", Type = SensitiveRuleTypes.AnyEmail, Enabled = true, DetectFragments = true }
            ]
        });
        var normalizer = new ContentNormalizer();
        var tracker = new FragmentSessionTracker(normalizer, store);
        return new ContentClassifier(store, normalizer, tracker);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("COMPANY_DLP_POLICY_PATH", _oldPolicyPath);
        try { Directory.Delete(_tempDirectory, true); } catch { }
    }
}
