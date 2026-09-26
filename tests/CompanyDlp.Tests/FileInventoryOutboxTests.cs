using CompanyDlp.Contracts;
using CompanyDlp.Core;
using CompanyDlp.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CompanyDlp.Tests;

// Uses the real on-disk queue (same PolicyStore/MachineDataProtector wiring as production, Development
// mode) rather than a fake - matches this codebase's existing convention for file-backed stores (see
// EncryptedFileHashStoreTests). Assertions are written to tolerate other items already sitting in the
// same shared pending/dead-letter directories (from a previous test or a real running agent on this
// machine) by filtering down to each test's own known ChangeIds rather than asserting exact counts.
public sealed class FileInventoryOutboxTests
{
    private static FileInventoryOutbox NewOutbox()
    {
        var policyStore = new PolicyStore(new MachineDataProtector(), NullLogger<PolicyStore>.Instance);
        var protector = new MachineDataProtector();
        return new FileInventoryOutbox(policyStore, protector, NullLogger<FileInventoryOutbox>.Instance);
    }

    private static FileInventoryChangeEnvelope MakeChange(DateTimeOffset occurredAtUtc, string path = "C:\\watched\\test.docx") => new()
    {
        ChangeId = Guid.NewGuid(),
        ChangeType = FileInventoryChangeTypes.Created,
        FilePath = path,
        FileHash = new string('a', 64),
        SizeBytes = 100,
        ClassificationTier = "Public",
        Provenance = "SelfCreated",
        IsProtected = false,
        OccurredAtUtc = occurredAtUtc
    };

    [Fact]
    public async Task EnqueueThenReadBatch_ContainsTheEnqueuedItem()
    {
        var outbox = NewOutbox();
        var change = MakeChange(DateTimeOffset.UtcNow);

        await outbox.EnqueueAsync(change);
        var items = await outbox.ReadBatchAsync(500, CancellationToken.None);

        var found = items.SingleOrDefault(i => i.Change.ChangeId == change.ChangeId);
        Assert.NotNull(found);
        Assert.Equal(change.FilePath, found!.Change.FilePath);
        Assert.Equal(change.ClassificationTier, found.Change.ClassificationTier);

        // Cleanup so this test doesn't leave a permanent pending item behind for other runs.
        outbox.MarkDelivered(items, new HashSet<Guid> { change.ChangeId });
    }

    [Fact]
    public async Task ReadBatch_ReturnsItemsInFifoOrder_ByOccurredAtUtc()
    {
        var outbox = NewOutbox();
        var baseline = DateTimeOffset.UtcNow;
        var first = MakeChange(baseline);
        var second = MakeChange(baseline.AddSeconds(1));
        var third = MakeChange(baseline.AddSeconds(2));
        var ourIds = new[] { first.ChangeId, second.ChangeId, third.ChangeId };

        // Enqueued out of chronological order - FIFO ordering must come from OccurredAtUtc-derived
        // filenames, not enqueue call order.
        await outbox.EnqueueAsync(third);
        await outbox.EnqueueAsync(first);
        await outbox.EnqueueAsync(second);

        var items = await outbox.ReadBatchAsync(500, CancellationToken.None);
        var ours = items.Where(i => ourIds.Contains(i.Change.ChangeId)).ToList();

        Assert.Equal(3, ours.Count);
        Assert.Equal(first.ChangeId, ours[0].Change.ChangeId);
        Assert.Equal(second.ChangeId, ours[1].Change.ChangeId);
        Assert.Equal(third.ChangeId, ours[2].Change.ChangeId);

        outbox.MarkDelivered(items, new HashSet<Guid>(ourIds));
    }

    [Fact]
    public async Task MarkDelivered_RemovesOnlyTheAcceptedItem_LeavesOthersPending()
    {
        var outbox = NewOutbox();
        var delivered = MakeChange(DateTimeOffset.UtcNow);
        var stillPending = MakeChange(DateTimeOffset.UtcNow.AddSeconds(1));

        await outbox.EnqueueAsync(delivered);
        await outbox.EnqueueAsync(stillPending);
        var items = await outbox.ReadBatchAsync(500, CancellationToken.None);

        outbox.MarkDelivered(
            items.Where(i => i.Change.ChangeId == delivered.ChangeId),
            new HashSet<Guid> { delivered.ChangeId });

        var remaining = await outbox.ReadBatchAsync(500, CancellationToken.None);
        Assert.DoesNotContain(remaining, i => i.Change.ChangeId == delivered.ChangeId);
        Assert.Contains(remaining, i => i.Change.ChangeId == stillPending.ChangeId);

        // Cleanup.
        var stillPendingItem = remaining.Single(i => i.Change.ChangeId == stillPending.ChangeId);
        outbox.MarkDelivered(new[] { stillPendingItem }, new HashSet<Guid> { stillPending.ChangeId });
    }

    [Fact]
    public async Task MarkPermanentlyRejected_RemovesItemFromPending()
    {
        var outbox = NewOutbox();
        var change = MakeChange(DateTimeOffset.UtcNow);

        await outbox.EnqueueAsync(change);
        var items = await outbox.ReadBatchAsync(500, CancellationToken.None);
        var item = items.Single(i => i.Change.ChangeId == change.ChangeId);

        outbox.MarkPermanentlyRejected(item, "TestRejectionReason");

        var afterRejection = await outbox.ReadBatchAsync(500, CancellationToken.None);
        Assert.DoesNotContain(afterRejection, i => i.Change.ChangeId == change.ChangeId);
    }
}
