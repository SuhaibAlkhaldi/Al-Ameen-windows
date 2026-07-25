using System.Security.Cryptography;
using System.Text.Json;
using CompanyDlp.AdminApi.Configuration;
using CompanyDlp.AdminApi.Services;
using CompanyDlp.Contracts;
using CompanyDlp.Service;
using Microsoft.Extensions.Options;
using Xunit;

namespace CompanyDlp.Tests;

public sealed class CentralAdministrationTests
{
    [Fact]
    public void DefaultPolicyFactory_IsFailClosedForRestrictedActions()
    {
        var tenantId = Guid.NewGuid();
        var policy = DefaultPolicyFactory.Create(tenantId, new PolicyDeliveryOptions
        {
            Mode = "Production",
            PublicBaseUrl = "https://dlp.example.test"
        });

        Assert.Equal(tenantId, policy.Backend.TenantId);
        Assert.False(policy.Permissions.DefaultPermissions[ActionKeys.ScreenCapture]);
        Assert.False(policy.Permissions.DefaultPermissions[ActionKeys.BrowserUpload]);
        Assert.False(policy.Permissions.DefaultPermissions[ActionKeys.UsbDeviceConnect]);
        Assert.True(policy.Permissions.DefaultPermissions[ActionKeys.FileEncrypt]);
    }

    [Fact]
    public void PolicySigningService_ProducesAgentCompatibleEcdsaSignature()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var service = new PolicySigningService(Options.Create(new PolicyDeliveryOptions
        {
            Mode = "Production",
            PublicBaseUrl = "https://dlp.example.test",
            SigningPrivateKeyPem = ecdsa.ExportPkcs8PrivateKeyPem()
        }));
        var tenantId = Guid.NewGuid();
        var snapshot = new SignedPolicySnapshot
        {
            PolicyId = Guid.NewGuid(),
            Version = 7,
            TenantId = tenantId,
            DeviceId = Guid.NewGuid(),
            IssuedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
            Policy = DefaultPolicyFactory.Create(tenantId, new PolicyDeliveryOptions
            {
                Mode = "Production",
                PublicBaseUrl = "https://dlp.example.test"
            })
        };

        service.Sign(snapshot);
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

        using var verifier = ECDsa.Create();
        verifier.ImportFromPem(service.GetPublicKeyPem());
        Assert.Equal("ECDSA-SHA256", snapshot.SignatureAlgorithm);
        Assert.True(verifier.VerifyData(
            payload,
            Convert.FromBase64String(snapshot.SignatureBase64),
            HashAlgorithmName.SHA256));
    }

    [Fact]
    public void TenantPolicySanitizer_NormalizesUnknownAndMissingDefaultsFailClosed()
    {
        var policy = new DlpPolicy
        {
            Permissions = new PermissionPolicy
            {
                DefaultPermissions = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                {
                    ["SCREEN.CAPTURE"] = true,
                    ["unknown.action"] = true
                }
            }
        };

        var error = TenantPolicySanitizer.Normalize(policy);

        Assert.Null(error);
        Assert.Equal(ActionKeys.All.Count, policy.Permissions.DefaultPermissions.Count);
        Assert.True(policy.Permissions.DefaultPermissions[ActionKeys.ScreenCapture]);
        Assert.False(policy.Permissions.DefaultPermissions[ActionKeys.BrowserUpload]);
        Assert.DoesNotContain("unknown.action", policy.Permissions.DefaultPermissions.Keys);
    }

    [Fact]
    public async Task PolicyRefreshSignal_CoalescesRepeatedRequests()
    {
        var signal = new PolicyRefreshSignal();
        signal.RequestRefresh();
        signal.RequestRefresh();

        Assert.True(await signal.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None));
        Assert.False(await signal.WaitAsync(TimeSpan.FromMilliseconds(10), CancellationToken.None));
    }
}

