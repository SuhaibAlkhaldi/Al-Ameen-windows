using System.Security.Cryptography;
using System.Text.Json;
using CompanyDlp.Contracts;

namespace CompanyDlp.Service;

public sealed class PolicySnapshotValidator(PolicyStore policyStore)
{
    public bool TryValidate(
        SignedPolicySnapshot snapshot,
        AgentIdentity identity,
        DateTimeOffset nowUtc,
        out string failureReason)
    {
        failureReason = "";
        var backend = policyStore.Get().Backend;

        if (snapshot.PolicyId == Guid.Empty || snapshot.Version <= 0)
            return Fail("InvalidPolicyIdentity", out failureReason);
        if (snapshot.TenantId != identity.TenantId)
            return Fail("TenantMismatch", out failureReason);
        if (snapshot.DeviceId != identity.DeviceId)
            return Fail("DeviceMismatch", out failureReason);
        if (snapshot.IssuedAtUtc > nowUtc.AddMinutes(5))
            return Fail("PolicyIssuedInFuture", out failureReason);
        if (snapshot.ExpiresAtUtc <= nowUtc)
            return Fail("PolicyExpired", out failureReason);

        if (snapshot.SignatureBase64.Equals("DEVELOPMENT-UNSIGNED", StringComparison.Ordinal)
            && backend.AllowUnsignedDevelopmentPolicy
            && snapshot.Policy.Runtime.Mode.Equals("Development", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!snapshot.SignatureAlgorithm.Equals("ECDSA-SHA256", StringComparison.OrdinalIgnoreCase))
            return Fail("UnsupportedPolicySignatureAlgorithm", out failureReason);

        if (string.IsNullOrWhiteSpace(backend.PolicySigningPublicKeyPem))
            return Fail("MissingPolicySigningPublicKey", out failureReason);

        try
        {
            var signature = Convert.FromBase64String(snapshot.SignatureBase64);
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
            ecdsa.ImportFromPem(backend.PolicySigningPublicKeyPem);
            if (!ecdsa.VerifyData(payload, signature, HashAlgorithmName.SHA256))
                return Fail("InvalidPolicySignature", out failureReason);
            return true;
        }
        catch (Exception)
        {
            return Fail("PolicySignatureValidationFailed", out failureReason);
        }
    }

    private static bool Fail(string reason, out string failureReason)
    {
        failureReason = reason;
        return false;
    }
}
