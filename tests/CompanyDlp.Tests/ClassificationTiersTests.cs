using CompanyDlp.Contracts;
using Xunit;

namespace CompanyDlp.Tests;

public sealed class ClassificationTiersTests
{
    [Theory]
    [InlineData(ClassificationTiers.Public, 0)]
    [InlineData(ClassificationTiers.Internal, 1)]
    [InlineData(ClassificationTiers.Secret, 2)]
    [InlineData(ClassificationTiers.VerySecret, 3)]
    public void RankOf_OrdersKnownTiersLeastToMostSensitive(string tier, int expectedRank)
    {
        Assert.Equal(expectedRank, ClassificationTiers.RankOf(tier));
    }

    [Fact]
    public void RankOf_FailsClosedToTheMostSensitiveRankForAnUnrecognizedValue()
    {
        // Regression guard: an unrecognized classification (e.g. the BlockAll provider's
        // placeholder "Sensitive" string, used before the real AI provider is configured) must rank
        // as the most sensitive tier, not silently resolve to Public's rank 0 - that would defeat
        // PermissionEvaluator's fail-closed handling of unclassified/unrecognized files.
        Assert.Equal(ClassificationTiers.RankOf(ClassificationTiers.VerySecret), ClassificationTiers.RankOf("Sensitive"));
        Assert.Equal(ClassificationTiers.RankOf(ClassificationTiers.VerySecret), ClassificationTiers.RankOf(""));
    }
}
