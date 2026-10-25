using System.Net;
using System.Net.Http.Json;
using CompanyDlp.Contracts;
using CompanyDlp.Core;
using CompanyDlp.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CompanyDlp.Tests;

// Covers the uninstall/reinstall re-enrollment fix's agent-side half: BackendApiClient.UnenrollAsync
// must only delete the locally stored device credential once the backend has actually confirmed the
// device is retired - never on a failed/offline call, so a later retry (once connectivity returns)
// can still present the same credential and try again.
public sealed class BackendApiClientUnenrollTests
{
    [Fact]
    public async Task UnenrollAsync_PostsToUnenrollEndpoint()
    {
        var (client, credentialStore, credentialName, handler) = CreateClient();
        credentialStore.Save(credentialName, "test-token");
        handler.NextResponse = SuccessResponse();

        await client.UnenrollAsync(CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/api/v1/agent/unenroll", handler.LastRequest.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task UnenrollAsync_OnSuccess_DeletesStoredCredential()
    {
        var (client, credentialStore, credentialName, handler) = CreateClient();
        credentialStore.Save(credentialName, "test-token");
        handler.NextResponse = SuccessResponse();

        await client.UnenrollAsync(CancellationToken.None);

        Assert.Null(credentialStore.Load(credentialName));
    }

    [Fact]
    public async Task UnenrollAsync_OnFailureResponse_ThrowsAndLeavesStoredCredentialIntact()
    {
        var (client, credentialStore, credentialName, handler) = CreateClient();
        credentialStore.Save(credentialName, "test-token");
        handler.NextResponse = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = JsonContent.Create(new
            {
                success = false,
                messageEn = "Device is not active.",
                messageAr = "الجهاز غير نشط"
            })
        };

        await Assert.ThrowsAsync<HttpRequestException>(() => client.UnenrollAsync(CancellationToken.None));

        Assert.Equal("test-token", credentialStore.Load(credentialName));
    }

    private static HttpResponseMessage SuccessResponse() => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new AgentUnenrollResponse { Unenrolled = true })
    };

    private static (BackendApiClient Client, AgentCredentialStore CredentialStore, string CredentialName, CapturingHandler Handler) CreateClient()
    {
        var dataProtector = new MachineDataProtector();
        var policyStore = new PolicyStore(dataProtector, NullLogger<PolicyStore>.Instance);
        var identityProvider = new AgentIdentityProvider(policyStore, NullLogger<AgentIdentityProvider>.Instance);
        var credentialStore = new AgentCredentialStore(policyStore, dataProtector);
        var authenticator = new BackendRequestAuthenticator(policyStore, identityProvider, credentialStore);

        var handler = new CapturingHandler();
        var client = new BackendApiClient(new CapturingHttpClientFactory(handler), policyStore, authenticator, credentialStore);

        return (client, credentialStore, policyStore.Get().Backend.CredentialName, handler);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public HttpResponseMessage NextResponse { get; set; } = new(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(NextResponse);
        }
    }

    private sealed class CapturingHttpClientFactory(HttpMessageHandler handler) : System.Net.Http.IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
