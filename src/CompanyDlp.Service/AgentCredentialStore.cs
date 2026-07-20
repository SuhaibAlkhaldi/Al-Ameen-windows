using System.Security.Cryptography;
using System.Text;

namespace CompanyDlp.Service;

public sealed class AgentCredentialStore(
    PolicyStore policyStore,
    MachineDataProtector dataProtector)
{
    private const string PurposePrefix = "CompanyDlp.AgentCredential.v1:";

    public string? Load(string credentialName)
    {
        var path = GetPath(credentialName);
        if (!File.Exists(path)) return null;
        var protectedBytes = File.ReadAllBytes(path);
        try
        {
            var clearBytes = dataProtector.Unprotect(protectedBytes, PurposePrefix + NormalizeName(credentialName));
            try { return Encoding.UTF8.GetString(clearBytes); }
            finally { CryptographicOperations.ZeroMemory(clearBytes); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    public void Save(string credentialName, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A non-empty credential is required.", nameof(value));
        var path = GetPath(credentialName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var clearBytes = Encoding.UTF8.GetBytes(value);
        try
        {
            var protectedBytes = dataProtector.Protect(clearBytes, PurposePrefix + NormalizeName(credentialName));
            try
            {
                var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
                File.WriteAllBytes(temporary, protectedBytes);
                File.Move(temporary, path, true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clearBytes);
        }
    }

    public void Delete(string credentialName)
    {
        var path = GetPath(credentialName);
        if (File.Exists(path)) File.Delete(path);
    }

    private string GetPath(string credentialName)
    {
        var mode = policyStore.Get().Runtime.Mode;
        var root = mode.Equals("Production", StringComparison.OrdinalIgnoreCase)
            ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "CompanyDlp", "Credentials", NormalizeName(credentialName) + ".bin");
    }

    private static string NormalizeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var normalized = new string((value ?? "credential").Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(normalized) ? "credential" : normalized;
    }
}
