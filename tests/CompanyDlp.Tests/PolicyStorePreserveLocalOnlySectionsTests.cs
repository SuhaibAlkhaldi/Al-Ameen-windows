using CompanyDlp.Contracts;
using CompanyDlp.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CompanyDlp.Tests;

// Regression coverage for the fix that stops a remote policy snapshot from ever overwriting this
// device's own Backend/Runtime settings (which server to talk to, whether unsigned policies are
// trusted, the signing public key, sync intervals) - these are inherently local-installation concerns,
// and must never change just because a backend (correctly configured or not) sent something different.
// Other sections (e.g. Permissions.Grants) must continue to update normally from a remote snapshot -
// this is what makes the whole policy-push mechanism (grants, revocations, watermark toggles) work at
// all, so the fix must not accidentally freeze everything.
public sealed class PolicyStorePreserveLocalOnlySectionsTests
{
    private static PolicyStore NewPolicyStore() =>
        new(new MachineDataProtector(), NullLogger<PolicyStore>.Instance);

    private static SignedPolicySnapshot BuildSnapshotDifferingInBackendAndRuntime(DlpPolicy localPolicy, long version)
    {
        return new SignedPolicySnapshot
        {
            PolicyId = Guid.NewGuid(),
            Version = version,
            TenantId = Guid.NewGuid(),
            DeviceId = Guid.NewGuid(),
            IssuedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(7),
            SignatureAlgorithm = "DEVELOPMENT",
            SignatureBase64 = "DEVELOPMENT-UNSIGNED",
            Policy = new DlpPolicy
            {
                PolicyVersion = $"central-{version}",
                Enabled = true,
                Runtime = new RuntimePolicy
                {
                    // Deliberately different from whatever the local policy actually has, so the test
                    // can prove these do NOT propagate.
                    Mode = "SomethingElseEntirely",
                    PolicyReapplySeconds = 999
                },
                Backend = new BackendPolicy
                {
                    Mode = "SomethingElseEntirely",
                    BaseUrl = "https://attacker-controlled.example.invalid",
                    AllowUnsignedDevelopmentPolicy = !localPolicy.Backend.AllowUnsignedDevelopmentPolicy,
                    PolicySigningPublicKeyPem = "not-the-real-public-key"
                },
                Permissions = new PermissionPolicy
                {
                    DefaultPermissions = localPolicy.Permissions.DefaultPermissions,
                    Grants =
                    [
                        new PermissionGrant
                        {
                            ActionKey = ActionKeys.UsbDeviceConnect,
                            Allowed = true,
                            SubjectType = PermissionSubjectTypes.DeviceId,
                            SubjectId = Guid.NewGuid().ToString(),
                            Source = PermissionSources.PermanentPolicy,
                            Priority = 500
                        }
                    ]
                }
            }
        };
    }

    [Fact]
    public void ApplyRemoteSnapshot_DoesNotChangeLocalBackendOrRuntime_ButDoesUpdatePermissions()
    {
        var policyStore = NewPolicyStore();
        var localPolicyBefore = policyStore.Get();
        var originalBackendMode = localPolicyBefore.Backend.Mode;
        var originalBackendBaseUrl = localPolicyBefore.Backend.BaseUrl;
        var originalAllowUnsigned = localPolicyBefore.Backend.AllowUnsignedDevelopmentPolicy;
        var originalRuntimeMode = localPolicyBefore.Runtime.Mode;
        var originalReapplySeconds = localPolicyBefore.Runtime.PolicyReapplySeconds;

        var snapshot = BuildSnapshotDifferingInBackendAndRuntime(localPolicyBefore, version: 1);

        policyStore.ApplyRemoteSnapshot(snapshot);
        var policyAfter = policyStore.Get();

        // Backend/Runtime: unchanged, despite the snapshot carrying different values.
        Assert.Equal(originalBackendMode, policyAfter.Backend.Mode);
        Assert.Equal(originalBackendBaseUrl, policyAfter.Backend.BaseUrl);
        Assert.Equal(originalAllowUnsigned, policyAfter.Backend.AllowUnsignedDevelopmentPolicy);
        Assert.NotEqual("https://attacker-controlled.example.invalid", policyAfter.Backend.BaseUrl);
        Assert.Equal(originalRuntimeMode, policyAfter.Runtime.Mode);
        Assert.Equal(originalReapplySeconds, policyAfter.Runtime.PolicyReapplySeconds);
        Assert.NotEqual(999, policyAfter.Runtime.PolicyReapplySeconds);

        // Permissions: DOES update normally - the whole point of remote policy sync still works.
        Assert.Single(policyAfter.Permissions.Grants);
        Assert.Equal(ActionKeys.UsbDeviceConnect, policyAfter.Permissions.Grants[0].ActionKey);
        Assert.Equal(1, policyStore.CurrentRemoteVersion);
    }

    [Fact]
    public void ApplyRemoteSnapshot_AppliedTwice_StillNeverLeaksBackendOrRuntime()
    {
        var policyStore = NewPolicyStore();
        var localPolicyBefore = policyStore.Get();
        var originalBackendBaseUrl = localPolicyBefore.Backend.BaseUrl;

        policyStore.ApplyRemoteSnapshot(BuildSnapshotDifferingInBackendAndRuntime(localPolicyBefore, version: 1));
        policyStore.ApplyRemoteSnapshot(BuildSnapshotDifferingInBackendAndRuntime(localPolicyBefore, version: 2));

        var policyAfter = policyStore.Get();
        Assert.Equal(originalBackendBaseUrl, policyAfter.Backend.BaseUrl);
        Assert.Equal(2, policyStore.CurrentRemoteVersion);
    }
}
