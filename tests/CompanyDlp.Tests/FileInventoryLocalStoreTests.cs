using CompanyDlp.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CompanyDlp.Tests;

public sealed class FileInventoryLocalStoreTests
{
    private static FileInventoryLocalStore NewStore()
    {
        var policyStore = new PolicyStore(new MachineDataProtector(), NullLogger<PolicyStore>.Instance);
        return new FileInventoryLocalStore(policyStore, NullLogger<FileInventoryLocalStore>.Instance);
    }

    private static string UniqueTestPath(string label) =>
        Path.Combine(Path.GetTempPath(), $"fi-local-store-test-{label}-{Guid.NewGuid():N}.txt");

    private static FileInventoryLocalEntry MakeEntry(string path) => new(
        path, FileHash: new string('a', 64), SizeBytes: 100, ClassificationTier: "Public",
        Provenance: "SelfCreated", IsProtected: false, LastWriteTimeUtc: DateTimeOffset.UtcNow, LastSyncedAtUtc: DateTimeOffset.UtcNow);

    [Fact]
    public void TryGet_ReturnsNull_ForAPathNeverSet()
    {
        var store = NewStore();
        Assert.Null(store.TryGet(UniqueTestPath("nonexistent")));
    }

    [Fact]
    public void SetThenTryGet_RoundTripsTheEntry()
    {
        var store = NewStore();
        var path = UniqueTestPath("roundtrip");

        store.Set(MakeEntry(path) with { ClassificationTier = "Secret" });
        var found = store.TryGet(path);

        Assert.NotNull(found);
        Assert.Equal("Secret", found!.ClassificationTier);
    }

    [Fact]
    public void Set_PersistsAcrossANewStoreInstance()
    {
        // The first Set() call on a fresh store always saves immediately (SaveThrottled's "first call
        // after a quiet period" fast path - see the class comment), so this doesn't need Flush().
        var path = UniqueTestPath("persist");
        NewStore().Set(MakeEntry(path) with { ClassificationTier = "Very_Secret" });

        var reloaded = NewStore().TryGet(path);

        Assert.NotNull(reloaded);
        Assert.Equal("Very_Secret", reloaded!.ClassificationTier);
    }

    [Fact]
    public void Remove_DeletesTheEntry_Immediately_NotThrottled()
    {
        var store = NewStore();
        var path = UniqueTestPath("remove");
        store.Set(MakeEntry(path));
        Assert.NotNull(store.TryGet(path));

        store.Remove(path);

        Assert.Null(store.TryGet(path));
        // Un-throttled (unlike Set) - a fresh instance must see the removal too, without any Flush().
        Assert.Null(NewStore().TryGet(path));
    }

    [Fact]
    public void SecondSetWithinThrottleWindow_IsNotPersistedUntilFlush()
    {
        var store = NewStore();
        var firstPath = UniqueTestPath("throttle-first");
        var secondPath = UniqueTestPath("throttle-second");

        // First Set() on a fresh instance saves immediately (see the persistence test above).
        store.Set(MakeEntry(firstPath));

        // Second Set(), milliseconds later - still well inside the 2-second throttle window, so this
        // one should NOT have reached disk yet.
        store.Set(MakeEntry(secondPath));

        Assert.Null(NewStore().TryGet(secondPath)); // not yet on disk
        Assert.NotNull(store.TryGet(secondPath)); // but visible in-memory on the same instance immediately

        store.Flush();

        Assert.NotNull(NewStore().TryGet(secondPath)); // now on disk after Flush()
    }

    [Fact]
    public void InitialSyncCompleted_DefaultsFalse_AndPersistsAcrossInstancesWhenSetTrue()
    {
        var policyStore = new PolicyStore(new MachineDataProtector(), NullLogger<PolicyStore>.Instance);
        var store = new FileInventoryLocalStore(policyStore, NullLogger<FileInventoryLocalStore>.Instance);

        // Start from a known false state in case a previous run left the marker file behind.
        store.InitialSyncCompleted = false;
        Assert.False(store.InitialSyncCompleted);
        Assert.False(new FileInventoryLocalStore(policyStore, NullLogger<FileInventoryLocalStore>.Instance).InitialSyncCompleted);

        store.InitialSyncCompleted = true;

        Assert.True(store.InitialSyncCompleted);
        Assert.True(new FileInventoryLocalStore(policyStore, NullLogger<FileInventoryLocalStore>.Instance).InitialSyncCompleted);

        // Leave it reset so this test doesn't leak a "completed" marker into whatever runs next.
        store.InitialSyncCompleted = false;
    }
}
