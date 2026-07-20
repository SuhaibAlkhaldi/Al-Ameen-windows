using System.Net.Http.Headers;
using CompanyDlp.Contracts;

namespace CompanyDlp.Service;

public sealed class BackendRequestAuthenticator(
    PolicyStore policyStore,
    AgentIdentityProvider identityProvider,
    AgentCredentialStore credentialStore)
{
    public void Apply(HttpRequestMessage request)
    {
        var backend = policyStore.Get().Backend;
        var identity = identityProvider.Get();
        request.Headers.TryAddWithoutValidation("X-CompanyDlp-TenantId", identity.TenantId.ToString("D"));
        request.Headers.TryAddWithoutValidation("X-CompanyDlp-DeviceId", identity.DeviceId.ToString("D"));
        request.Headers.TryAddWithoutValidation("X-CompanyDlp-AgentVersion", identity.AgentVersion);

        if (backend.AuthenticationMode.Equals(
            BackendAuthenticationModes.DevelopmentNone,
            StringComparison.OrdinalIgnoreCase))
            return;

        if (!backend.AuthenticationMode.Equals(
            BackendAuthenticationModes.DeviceBearerToken,
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unsupported backend authentication mode: {backend.AuthenticationMode}");

        var accessToken = credentialStore.Load(backend.CredentialName);
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException(
                "The Company DLP agent is not enrolled. A DPAPI-protected device access token is required.");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }
}
