using System.Security.Cryptography;
using CompanyDlp.Contracts;

namespace CompanyDlp.Service;

public sealed class FileKeyProtector(
    PolicyStore policyStore,
    AgentIdentityProvider identityProvider,
    MachineDataProtector machineDataProtector,
    BackendApiClient backendApiClient) : IFileKeyProtector
{
    public async Task<WrappedFileKey> WrapAsync(Guid fileId, byte[] plainKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plainKey);
        if (plainKey.Length != 32) throw new CryptographicException("File encryption keys must be 256 bits.");

        var provider = policyStore.Get().FileProtection.KeyProvider;
        if (provider.Equals("LocalMachineDpapi", StringComparison.OrdinalIgnoreCase))
        {
            var wrapped = machineDataProtector.Protect(plainKey, Purpose(fileId));
            try
            {
                return new WrappedFileKey
                {
                    Provider = "LocalMachineDpapi",
                    KeyId = "local-machine",
                    WrappedKeyBase64 = Convert.ToBase64String(wrapped)
                };
            }
            finally
            {
                CryptographicOperations.ZeroMemory(wrapped);
            }
        }

        if (provider.Equals("BackendKms", StringComparison.OrdinalIgnoreCase))
        {
            var identity = identityProvider.Get();
            var response = await backendApiClient.WrapFileKeyAsync(new FileKeyWrapRequest
            {
                TenantId = identity.TenantId,
                DeviceId = identity.DeviceId,
                FileId = fileId,
                PlainKeyBase64 = Convert.ToBase64String(plainKey)
            }, cancellationToken);
            return new WrappedFileKey
            {
                Provider = "BackendKms",
                KeyId = response.KeyId,
                WrappedKeyBase64 = response.WrappedKeyBase64
            };
        }

        throw new InvalidOperationException($"Unsupported file key provider: {provider}");
    }

    public async Task<byte[]> UnwrapAsync(Guid fileId, WrappedFileKey wrappedKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wrappedKey);
        if (wrappedKey.Provider.Equals("LocalMachineDpapi", StringComparison.OrdinalIgnoreCase))
        {
            var protectedBytes = Convert.FromBase64String(wrappedKey.WrappedKeyBase64);
            try
            {
                var clear = machineDataProtector.Unprotect(protectedBytes, Purpose(fileId));
                if (clear.Length != 32)
                {
                    CryptographicOperations.ZeroMemory(clear);
                    throw new CryptographicException("The unwrapped file key has an invalid length.");
                }
                return clear;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }

        if (wrappedKey.Provider.Equals("BackendKms", StringComparison.OrdinalIgnoreCase))
        {
            var identity = identityProvider.Get();
            var response = await backendApiClient.UnwrapFileKeyAsync(new FileKeyUnwrapRequest
            {
                TenantId = identity.TenantId,
                DeviceId = identity.DeviceId,
                FileId = fileId,
                KeyId = wrappedKey.KeyId,
                WrappedKeyBase64 = wrappedKey.WrappedKeyBase64
            }, cancellationToken);
            var clear = Convert.FromBase64String(response.PlainKeyBase64);
            if (clear.Length != 32)
            {
                CryptographicOperations.ZeroMemory(clear);
                throw new CryptographicException("Backend returned an invalid file key.");
            }
            return clear;
        }

        throw new CryptographicException($"Unsupported wrapped-key provider: {wrappedKey.Provider}");
    }

    private static string Purpose(Guid fileId) => $"CompanyDlp.FileKey.v2.{fileId:N}";
}
