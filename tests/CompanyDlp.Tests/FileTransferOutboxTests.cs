using CompanyDlp.Contracts;
using CompanyDlp.Core;
using CompanyDlp.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CompanyDlp.Tests;

// Same convention as FileInventoryOutboxTests: the real on-disk queue with the production protector wiring, and assertions
// filtered to this test's own send ids so other items in a shared pending/ folder do not break them.
public sealed class FileTransferOutboxTests
{
    private static FileTransferOutbox NewOutbox()
    {
        var policyStore = new PolicyStore(new MachineDataProtector(), NullLogger<PolicyStore>.Instance);
        return new FileTransferOutbox(policyStore, new MachineDataProtector(), NullLogger<FileTransferOutbox>.Instance);
    }

    private static FileTransferObservationNotice MakeObservation(Guid sendActionId, string recipient = "consultant@example.com") => new()
    {
        SendActionId = sendActionId,
        RecipientOrdinal = 0,
        Channel = "GMAIL",
        RecipientRole = "TO",
        RecipientKind = "EMAIL",
        RecipientValue = recipient,
        RecipientEvidenceType = "EMAIL_IN_SEND_FORM",
        FileName = "[Secret] plan.pdf",
        FileSizeBytes = 1234,
        FileHashBytes = new string('a', 64),
        ObservedAtUtc = DateTimeOffset.UtcNow,
        ExtensionVersion = "test"
    };

    [Fact]
    public async Task EnqueueThenReadBatch_RoundTripsTheObservation()
    {
        var outbox = NewOutbox();
        var observation = MakeObservation(Guid.NewGuid());

        await outbox.EnqueueAsync(observation);
        var items = await outbox.ReadBatchAsync(500, CancellationToken.None);

        var found = items.SingleOrDefault(item => item.Observation.SendActionId == observation.SendActionId);
        Assert.NotNull(found);
        Assert.Equal("consultant@example.com", found!.Observation.RecipientValue);
        Assert.Equal("[Secret] plan.pdf", found.Observation.FileName);
        Assert.Equal(1234, found.Observation.FileSizeBytes);

        outbox.MarkBatchDelivered(items.Where(item => item.Observation.SendActionId == observation.SendActionId));
    }

    [Fact]
    public async Task MarkBatchDelivered_RemovesTheDeliveredItem()
    {
        var outbox = NewOutbox();
        var observation = MakeObservation(Guid.NewGuid());
        await outbox.EnqueueAsync(observation);

        var items = await outbox.ReadBatchAsync(500, CancellationToken.None);
        var mine = items.Where(item => item.Observation.SendActionId == observation.SendActionId).ToList();
        outbox.MarkBatchDelivered(mine);

        var after = await outbox.ReadBatchAsync(500, CancellationToken.None);
        Assert.DoesNotContain(after, item => item.Observation.SendActionId == observation.SendActionId);
    }

    [Fact]
    public async Task ItemsNotMarkedDelivered_StayPendingForRetry()
    {
        var outbox = NewOutbox();
        var observation = MakeObservation(Guid.NewGuid());
        await outbox.EnqueueAsync(observation);

        // Simulate a failed send: the batch is read, but nothing is marked delivered.
        var items = await outbox.ReadBatchAsync(500, CancellationToken.None);
        Assert.Contains(items, item => item.Observation.SendActionId == observation.SendActionId);

        var retry = await outbox.ReadBatchAsync(500, CancellationToken.None);
        Assert.Contains(retry, item => item.Observation.SendActionId == observation.SendActionId);

        outbox.MarkBatchDelivered(retry.Where(item => item.Observation.SendActionId == observation.SendActionId));
    }
}
