using CompanyDlp.Contracts;

namespace CompanyDlp.Core;

public interface IFileKeyProtector
{
    Task<WrappedFileKey> WrapAsync(Guid fileId, byte[] plainKey, CancellationToken cancellationToken);
    Task<byte[]> UnwrapAsync(Guid fileId, WrappedFileKey wrappedKey, CancellationToken cancellationToken);
}
