using CompanyDlp.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CompanyDlp.Tests;

public sealed class EncryptedFileHashStoreTests
{
    private static EncryptedFileHashStore NewStore()
    {
        var policyStore = new PolicyStore(new MachineDataProtector(), NullLogger<PolicyStore>.Instance);
        return new EncryptedFileHashStore(policyStore, NullLogger<EncryptedFileHashStore>.Instance);
    }

    [Fact]
    public void TryGet_ReturnsNull_ForAFileIdNeverSet()
    {
        var store = NewStore();
        Assert.Null(store.TryGet(Guid.NewGuid()));
    }

    [Fact]
    public void SetThenTryGet_RoundTripsTheHash()
    {
        var store = NewStore();
        var fileId = Guid.NewGuid();
        var hash = new string('a', 64);

        store.Set(new EncryptedFileHashEntry(fileId, hash, DateTimeOffset.UtcNow));
        var found = store.TryGet(fileId);

        Assert.NotNull(found);
        Assert.Equal(hash, found!.FileHash);
    }

    [Fact]
    public void Set_PersistsAcrossANewStoreInstance()
    {
        var fileId = Guid.NewGuid();
        var hash = new string('b', 64);

        NewStore().Set(new EncryptedFileHashEntry(fileId, hash, DateTimeOffset.UtcNow));

        var reloaded = NewStore().TryGet(fileId);

        Assert.NotNull(reloaded);
        Assert.Equal(hash, reloaded!.FileHash);
    }
}
