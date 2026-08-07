using System.Net;
using CompanyDlp.Contracts;
using CompanyDlp.Core;
using CompanyDlp.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CompanyDlp.Tests;

// Verifies BackendApiClient.GetPolicyAsync's userSid parameter (added for shared-device support - see
// AgentPolicyService.BuildGrantsForDeviceAsync on the backend) only ever affects the outbound, UNSIGNED
// query string of the GET request the agent itself builds. The fake handler below returns 204 No
// Content - GetPolicyAsync's existing "nothing to apply" early-return path - so these tests never touch
// SignedPolicySnapshot deserialization or PolicySnapshotValidator at all, proving the change is confined
// to the request side and has zero interaction with policy signature verification.
public sealed class BackendApiClientPolicyRequestTests
{
    [Fact]
    public async Task GetPolicyAsync_WithUserSid_IncludesItInRequestQueryString()
    {
        var (client, identity, handler) = CreateClient();

        await client.GetPolicyAsync(identity, currentVersion: 5, userSid: "S-1-5-21-999", CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Contains("userSid=S-1-5-21-999", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task GetPolicyAsync_WithoutUserSid_OmitsUserSidFromRequestQueryString()
    {
        var (client, identity, handler) = CreateClient();

        await client.GetPolicyAsync(identity, currentVersion: 5, userSid: null, CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.DoesNotContain("userSid", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task GetPolicyAsync_StillSendsTenantDeviceAndVersion_RegardlessOfUserSid()
    {
        var (client, identity, handler) = CreateClient();

        await client.GetPolicyAsync(identity, currentVersion: 42, userSid: "S-1-5-21-999", CancellationToken.None);

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.Contains($"tenantId={identity.TenantId:D}", query);
        Assert.Contains($"deviceId={identity.DeviceId:D}", query);
        Assert.Contains("currentVersion=42", query);
    }

    private static (BackendApiClient Client, AgentIdentity Identity, CapturingHandler Handler) CreateClient()
    {
        var dataProtector = new MachineDataProtector();
        var policyStore = new PolicyStore(dataProtector, NullLogger<PolicyStore>.Instance);
        var identityProvider = new AgentIdentityProvider(policyStore, NullLogger<AgentIdentityProvider>.Instance);
        var credentialStore = new AgentCredentialStore(policyStore, dataProtector);
        var authenticator = new BackendRequestAuthenticator(policyStore, identityProvider, credentialStore);

        var handler = new CapturingHandler();
        var client = new BackendApiClient(new CapturingHttpClientFactory(handler), policyStore, authenticator, credentialStore);

        return (client, identityProvider.Get(), handler);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }

    private sealed class CapturingHttpClientFactory(HttpMessageHandler handler) : System.Net.Http.IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
