using CompanyDlp.Contracts;

namespace CompanyDlp.Service;

// Caps how much one file-inventory request can carry. ASP.NET's default request body limit on the backend is 30 MB,
// and captured file text (FileVersionTextCapture) can make a batch of 200 change envelopes far larger than that. A
// request over the limit is rejected whole, and the outbox would retry the same batch forever, stalling all inventory
// sync. Items past the budget are left in the outbox and sent in the next request, so none are dropped.
public static class FileInventoryPayloadBudget
{
    public const long MaxRequestPayloadBytes = 8L * 1024 * 1024;

    // The serializer escapes non-ASCII characters (for example Arabic text) as \uXXXX, which is six bytes each.
    // Costing every character at that worst case keeps the estimate above the real size for any text.
    private const long BytesPerTextCharWorstCase = 6;
    private const long EnvelopeOverheadBytes = 1024;

    public static long EstimateBytes(FileInventoryChangeEnvelope change) =>
        EnvelopeOverheadBytes + (long)(change.ExtractedText?.Length ?? 0) * BytesPerTextCharWorstCase;

    // Keeps the leading items whose estimated size fits the budget. The first item is always kept, even when it alone
    // is over the budget, so one oversized envelope can never stall the outbox.
    public static IReadOnlyList<T> TakeWithinBudget<T>(IReadOnlyList<T> items, Func<T, long> estimateBytes, long budgetBytes = MaxRequestPayloadBytes)
    {
        var taken = new List<T>(items.Count);
        long total = 0;

        foreach (var item in items)
        {
            var cost = estimateBytes(item);
            if (taken.Count > 0 && total + cost > budgetBytes) break;

            taken.Add(item);
            total += cost;
        }

        return taken;
    }
}
