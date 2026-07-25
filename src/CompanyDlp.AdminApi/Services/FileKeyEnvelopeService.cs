using Microsoft.AspNetCore.DataProtection;

namespace CompanyDlp.AdminApi.Services;

public sealed class FileKeyEnvelopeService(IDataProtectionProvider provider)
{
    private const string KeyId = "aspnet-dataprotection-v1";

    public (string KeyId, string WrappedKeyBase64) Wrap(
        Guid tenantId,
        Guid fileId,
        byte[] plainKey)
    {
        var protector = CreateProtector(tenantId, fileId);
        return (KeyId, Convert.ToBase64String(protector.Protect(plainKey)));
    }

    public byte[] Unwrap(
        Guid tenantId,
        Guid fileId,
        string keyId,
        string wrappedKeyBase64)
    {
        if (!string.Equals(keyId, KeyId, StringComparison.Ordinal))
            throw new InvalidOperationException("Unsupported file-key envelope provider.");
        if (string.IsNullOrWhiteSpace(wrappedKeyBase64))
            throw new FormatException("The wrapped key is required.");
        return CreateProtector(tenantId, fileId).Unprotect(Convert.FromBase64String(wrappedKeyBase64));
    }

    private IDataProtector CreateProtector(Guid tenantId, Guid fileId) =>
        provider.CreateProtector(
            "CompanyDlp.ServerFileKeyEnvelope.v1",
            tenantId.ToString("N"),
            fileId.ToString("N"));
}
