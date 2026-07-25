using System.Security.Cryptography;
using System.Text.Json;
using CompanyDlp.Contracts;

namespace CompanyDlp.AdminApi.Services;

public sealed class AuditIntegrityValidator
{
    public bool IsValid(SecurityEventEnvelope value)
    {
        var supplied = value.IntegrityHash ?? "";
        if (supplied.Length != 64 || !supplied.All(Uri.IsHexDigit)) return false;
        value.IntegrityHash = "";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonDefaults.Options);
            var expected = SHA256.HashData(bytes);
            var actual = Convert.FromHexString(supplied);
            try
            {
                return CryptographicOperations.FixedTimeEquals(expected, actual);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(expected);
                CryptographicOperations.ZeroMemory(actual);
            }
        }
        finally
        {
            value.IntegrityHash = supplied;
        }
    }
}
