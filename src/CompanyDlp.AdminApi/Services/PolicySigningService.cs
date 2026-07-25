using System.Security.Cryptography;
using System.Text.Json;
using CompanyDlp.AdminApi.Configuration;
using CompanyDlp.Contracts;
using Microsoft.Extensions.Options;

namespace CompanyDlp.AdminApi.Services;

public sealed class PolicySigningService(IOptions<PolicyDeliveryOptions> options)
{
    private readonly PolicyDeliveryOptions _options = options.Value;

    public void Sign(SignedPolicySnapshot snapshot)
    {
        var privateKeyPem = ResolvePrivateKeyPem();
        if (string.IsNullOrWhiteSpace(privateKeyPem))
        {
            if (_options.AllowUnsignedDevelopmentSnapshots
                && snapshot.Policy.Runtime.Mode.Equals("Development", StringComparison.OrdinalIgnoreCase))
            {
                snapshot.SignatureAlgorithm = "DEVELOPMENT";
                snapshot.SignatureBase64 = "DEVELOPMENT-UNSIGNED";
                return;
            }

            throw new InvalidOperationException(
                "A policy-signing ECDSA private key is required outside unsigned Development mode.");
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            snapshot.PolicyId,
            snapshot.Version,
            snapshot.TenantId,
            snapshot.DeviceId,
            snapshot.IssuedAtUtc,
            snapshot.ExpiresAtUtc,
            snapshot.Policy
        }, JsonDefaults.Options);
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(privateKeyPem);
        snapshot.SignatureAlgorithm = "ECDSA-SHA256";
        snapshot.SignatureBase64 = Convert.ToBase64String(
            ecdsa.SignData(payload, HashAlgorithmName.SHA256));
    }

    public string GetPublicKeyPem()
    {
        var privateKeyPem = ResolvePrivateKeyPem();
        if (string.IsNullOrWhiteSpace(privateKeyPem)) return "";
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(privateKeyPem);
        return ecdsa.ExportSubjectPublicKeyInfoPem();
    }

    private string ResolvePrivateKeyPem()
    {
        if (!string.IsNullOrWhiteSpace(_options.SigningPrivateKeyPem))
            return _options.SigningPrivateKeyPem.Replace("\\n", Environment.NewLine, StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(_options.SigningPrivateKeyPath))
            return File.ReadAllText(Path.GetFullPath(_options.SigningPrivateKeyPath));
        return "";
    }
}
