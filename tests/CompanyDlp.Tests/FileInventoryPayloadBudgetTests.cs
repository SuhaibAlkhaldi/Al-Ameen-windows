using CompanyDlp.Contracts;
using CompanyDlp.Core;
using CompanyDlp.Service;
using Xunit;

namespace CompanyDlp.Tests;

public sealed class FileInventoryPayloadBudgetTests
{
    [Fact]
    public void ItemsUnderTheBudget_AreAllKept()
    {
        var kept = FileInventoryPayloadBudget.TakeWithinBudget(new[] { 3L, 3L, 3L }, cost => cost, budgetBytes: 9);

        Assert.Equal(new[] { 3L, 3L, 3L }, kept);
    }

    [Fact]
    public void Budget_StopsBeforeTheItemThatWouldExceedIt()
    {
        var kept = FileInventoryPayloadBudget.TakeWithinBudget(new[] { 3L, 3L, 3L }, cost => cost, budgetBytes: 7);

        Assert.Equal(new[] { 3L, 3L }, kept);
    }

    [Fact]
    public void FirstItem_IsKeptEvenWhenItAloneIsOverBudget()
    {
        // Dropping it would leave the outbox stuck on the same oversized item forever.
        var kept = FileInventoryPayloadBudget.TakeWithinBudget(new[] { 50L, 1L }, cost => cost, budgetBytes: 10);

        Assert.Equal(new[] { 50L }, kept);
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        var kept = FileInventoryPayloadBudget.TakeWithinBudget(Array.Empty<long>(), cost => cost, budgetBytes: 10);

        Assert.Empty(kept);
    }

    [Fact]
    public void EstimateBytes_CountsEachTextCharacterAtSixBytes()
    {
        var change = new FileInventoryChangeEnvelope { ExtractedText = new string('a', 1000) };

        Assert.Equal(1024 + 1000 * 6, FileInventoryPayloadBudget.EstimateBytes(change));
    }

    [Fact]
    public void EstimateBytes_WithoutText_IsEnvelopeOverheadOnly()
    {
        Assert.Equal(1024, FileInventoryPayloadBudget.EstimateBytes(new FileInventoryChangeEnvelope()));
    }

    [Fact]
    public void LargestCapturedText_FitsWithinTheRequestBudget()
    {
        // A version at the capture cap, in the worst-case escaping, must still go out on its own with room to spare.
        var largest = new FileInventoryChangeEnvelope { ExtractedText = new string('a', FileVersionTextCapture.MaxCapturedTextChars) };

        Assert.True(FileInventoryPayloadBudget.EstimateBytes(largest) < FileInventoryPayloadBudget.MaxRequestPayloadBytes);
    }
}
