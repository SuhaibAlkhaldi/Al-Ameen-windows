using System.Security.Cryptography;

namespace CompanyDlp.AdminApi.Security;

public static class TokenUtilities
{
    public static string CreateOpaqueToken(int bytes = 48) => Base64UrlEncode(RandomNumberGenerator.GetBytes(bytes));

    public static string HashToken(string token)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(token);
        try
        {
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        try
        {
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
