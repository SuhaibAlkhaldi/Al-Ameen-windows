using System.Net;
using System.Net.Http.Json;
using CompanyDlp.Contracts;

namespace CompanyDlp.Service;

public sealed class BackendApiClient(
    IHttpClientFactory httpClientFactory,
    PolicyStore policyStore,
    BackendRequestAuthenticator authenticator,
    AgentCredentialStore credentialStore)
{
    public Task<AuditBatchResponse> SendAuditBatchAsync(
        AuditBatchRequest request,
        CancellationToken cancellationToken) =>
        SendJsonAsync<AuditBatchRequest, AuditBatchResponse>(
            HttpMethod.Post,
            "api/v1/agent/events/batch",
            request,
            cancellationToken);

    public Task<AgentHeartbeatResponse> SendHeartbeatAsync(
        AgentHeartbeatRequest request,
        CancellationToken cancellationToken) =>
        SendJsonAsync<AgentHeartbeatRequest, AgentHeartbeatResponse>(
            HttpMethod.Post,
            "api/v1/agent/heartbeat",
            request,
            cancellationToken);

    // userSid is the currently active interactive console user's SID (shared-device disambiguation on
    // the backend - see AgentPolicyService.BuildGrantsForDeviceAsync). This is an outbound, UNSIGNED
    // query parameter on a GET request the agent itself constructs - it has no relationship whatsoever
    // to the signed AgentDlpPolicyDto/SignedPolicySnapshot returned below, which is still verified by
    // PolicySnapshotValidator exactly as before this field existed.
    public async Task<SignedPolicySnapshot?> GetPolicyAsync(
        AgentIdentity identity,
        long currentVersion,
        string? userSid,
        CancellationToken cancellationToken)
    {
        var backend = policyStore.Get().Backend;
        using var timeout = CreateTimeout(backend.RequestTimeoutSeconds, cancellationToken);
        var client = CreateClient(backend);
        var path = $"api/v1/agent/policy?tenantId={identity.TenantId:D}&deviceId={identity.DeviceId:D}&currentVersion={currentVersion}";
        if (!string.IsNullOrWhiteSpace(userSid))
        {
            path += $"&userSid={Uri.EscapeDataString(userSid)}";
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        authenticator.Apply(request);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotModified)
            return null;

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SignedPolicySnapshot>(JsonDefaults.Options, timeout.Token);
    }

    public async Task<DictionaryRulesResponse?> GetDictionaryRulesAsync(
        AgentIdentity identity,
        CancellationToken cancellationToken)
    {
        var backend = policyStore.Get().Backend;
        using var timeout = CreateTimeout(backend.RequestTimeoutSeconds, cancellationToken);
        var client = CreateClient(backend);
        var path = $"api/v1/agent/dictionary-rules?tenantId={identity.TenantId:D}&deviceId={identity.DeviceId:D}";
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        authenticator.Apply(request);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DictionaryRulesResponse>(JsonDefaults.Options, timeout.Token);
    }

    public Task<FileKeyWrapResponse> WrapFileKeyAsync(
        FileKeyWrapRequest request,
        CancellationToken cancellationToken) =>
        SendJsonAsync<FileKeyWrapRequest, FileKeyWrapResponse>(
            HttpMethod.Post,
            "api/v1/agent/file-keys/wrap",
            request,
            cancellationToken);

    public Task<FileKeyUnwrapResponse> UnwrapFileKeyAsync(
        FileKeyUnwrapRequest request,
        CancellationToken cancellationToken) =>
        SendJsonAsync<FileKeyUnwrapRequest, FileKeyUnwrapResponse>(
            HttpMethod.Post,
            "api/v1/agent/file-keys/unwrap",
            request,
            cancellationToken);

    public Task<FileClassificationResult> ClassifyFileAsync(
        FileClassificationRequest request,
        CancellationToken cancellationToken) =>
        SendJsonAsync<FileClassificationRequest, FileClassificationResult>(
            HttpMethod.Post,
            policyStore.Get().FileClassification.BackendPath,
            request,
            cancellationToken,
            policyStore.Get().FileClassification.TimeoutSeconds);

    public async Task<AgentEnrollmentResponse> EnrollAsync(
        AgentEnrollmentRequest enrollment,
        CancellationToken cancellationToken)
    {
        var backend = policyStore.Get().Backend;
        using var timeout = CreateTimeout(backend.RequestTimeoutSeconds, cancellationToken);
        var client = CreateClient(backend);
        using var response = await client.PostAsJsonAsync(
            "api/v1/agent/enroll",
            enrollment,
            JsonDefaults.Options,
            timeout.Token);

        // Deliberately NOT response.EnsureSuccessStatusCode() - that throws a generic
        // "Response status code does not indicate success: 400 (Bad Request)" with no way to see
        // *why* the backend rejected the request (wrong/expired code, tenant mismatch, missing
        // lookup data, etc.). The backend always returns a bilingual ApiResponse body
        // (messageEn/messageAr) on failure - see AgentEnrollmentController/AgentEnrollmentService -
        // so read and surface it instead, same pattern SendJsonAsync already uses for every other
        // endpoint. Confirmed live (2026-08-24): diagnosing a real enrollment failure took a manual
        // raw HTTP call from PowerShell because this method was swallowing the actual reason.
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(timeout.Token);
            var safeErrorBody = errorBody.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (safeErrorBody.Length > 2000) safeErrorBody = safeErrorBody[..2000];
            throw new HttpRequestException(
                $"Agent enrollment failed with {(int)response.StatusCode} ({response.ReasonPhrase}). Response: {safeErrorBody}",
                inner: null,
                statusCode: response.StatusCode);
        }

        var result = await response.Content.ReadFromJsonAsync<AgentEnrollmentResponse>(JsonDefaults.Options, timeout.Token)
            ?? throw new InvalidOperationException("Backend returned an empty enrollment response.");
        if (string.IsNullOrWhiteSpace(result.AccessToken))
            throw new InvalidOperationException("Backend enrollment response did not contain a device access token.");

        credentialStore.Save(backend.CredentialName, result.AccessToken);
        return result;
    }

    private async Task<TResponse> SendJsonAsync<TRequest, TResponse>(
        HttpMethod method,
        string path,
        TRequest payload,
        CancellationToken cancellationToken,
        int? timeoutSeconds = null)
    {
        var backend = policyStore.Get().Backend;
        using var timeout = CreateTimeout(timeoutSeconds ?? backend.RequestTimeoutSeconds, cancellationToken);
        var client = CreateClient(backend);
        using var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(payload, options: JsonDefaults.Options)
        };
        authenticator.Apply(request);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            var safeBody = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (safeBody.Length > 2000) safeBody = safeBody[..2000];
            throw new HttpRequestException(
                $"Backend request {method} {path} failed with {(int)response.StatusCode} ({response.ReasonPhrase}). Response: {safeBody}",
                inner: null,
                statusCode: response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<TResponse>(JsonDefaults.Options, timeout.Token)
            ?? throw new InvalidOperationException($"Backend returned an empty response for {path}.");
    }

    private HttpClient CreateClient(BackendPolicy backend)
    {
        if (!backend.Enabled) throw new InvalidOperationException("Backend synchronization is disabled.");
        if (!Uri.TryCreate(backend.BaseUrl, UriKind.Absolute, out var baseUri))
            throw new InvalidOperationException("The configured Company DLP backend URL is invalid.");
        if (backend.Mode.Equals("Production", StringComparison.OrdinalIgnoreCase)
            && baseUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Production Company DLP backend communication requires HTTPS.");

        var client = httpClientFactory.CreateClient("CompanyDlp.Backend");
        client.BaseAddress = new Uri(baseUri.ToString().TrimEnd('/') + "/");
        return client;
    }

    private static CancellationTokenSource CreateTimeout(int seconds, CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(seconds, 3, 120)));
        return source;
    }
}
