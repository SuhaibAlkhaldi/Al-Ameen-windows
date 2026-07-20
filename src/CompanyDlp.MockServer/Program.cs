using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CompanyDlp.Contracts;
using CompanyDlp.BrowserBridge;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("COMPANY_DLP_MOCK_URL") ?? "http://127.0.0.1:5055");
var app = builder.Build();

var projectRoot = ResolveProjectRoot();
var policyPath = Environment.GetEnvironmentVariable("COMPANY_DLP_POLICY_PATH")
    ?? Path.Combine(projectRoot, "config", "policy.development.json");
var dataRoot = Environment.GetEnvironmentVariable("COMPANY_DLP_MOCK_DATA")
    ?? Path.Combine(projectRoot, ".development", "mock-server");
Directory.CreateDirectory(dataRoot);

var acceptedIds = LoadAcceptedEventIds(dataRoot);
var writeGate = new SemaphoreSlim(1, 1);

app.MapGet("/health", () => Results.Ok(new
{
    status = "Healthy",
    mode = "DevelopmentMock",
    policyPath,
    dataRoot,
    serverTimeUtc = DateTimeOffset.UtcNow
}));

app.MapPost("/api/v1/development/native-message", async (
    JsonElement message,
    CancellationToken cancellationToken) =>
{
    return await BrowserNativeMessageRouter.HandleAsync(
        message,
        sourceProcessNameOverride: "browser-development-http-fallback",
        sourceProcessIdOverride: null,
        cancellationToken: cancellationToken);
});

app.MapPost("/api/v1/agent/enroll", (AgentEnrollmentRequest request) =>
{
    if (request.DeviceId == Guid.Empty || request.TenantId == Guid.Empty)
        return Results.BadRequest(new { message = "tenantId and deviceId are required." });

    return Results.Ok(new AgentEnrollmentResponse
    {
        AccessToken = "development-token-" + request.DeviceId.ToString("N"),
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(30)
    });
});

app.MapPost("/api/v1/agent/file-classification", (FileClassificationRequest request) =>
{
    return Results.Ok(new FileClassificationResult
    {
        RequestId = request.RequestId,
        IsAllowed = false,
        IsSensitive = true,
        Classification = "Sensitive",
        ReasonCode = "DevelopmentMockBlockAll",
        Provider = FileClassificationProviders.BlockAll,
        EvaluatedAtUtc = DateTimeOffset.UtcNow
    });
});

app.MapPost("/api/v1/agent/events/batch", async (AuditBatchRequest request, CancellationToken cancellationToken) =>
{
    var response = new AuditBatchResponse();
    foreach (var securityEvent in request.Events)
    {
        if (securityEvent.EventId == Guid.Empty
            || securityEvent.DeviceId != request.DeviceId
            || securityEvent.TenantId != request.TenantId)
        {
            response.RejectedEvents.Add(new RejectedAuditEvent
            {
                EventId = securityEvent.EventId,
                ReasonCode = "InvalidEventIdentity",
                Retryable = false
            });
            continue;
        }

        if (!HasValidIntegrityHash(securityEvent))
        {
            response.RejectedEvents.Add(new RejectedAuditEvent
            {
                EventId = securityEvent.EventId,
                ReasonCode = "IntegrityHashMismatch",
                Retryable = false
            });
            continue;
        }

        if (!acceptedIds.TryAdd(securityEvent.EventId, 0))
        {
            response.DuplicateEventIds.Add(securityEvent.EventId);
            continue;
        }

        await AppendJsonLineAsync(
            Path.Combine(dataRoot, $"security-events-{DateTime.UtcNow:yyyy-MM-dd}.jsonl"),
            securityEvent,
            writeGate,
            cancellationToken);
        response.AcceptedEventIds.Add(securityEvent.EventId);
    }

    return Results.Ok(response);
});

app.MapPost("/api/v1/agent/heartbeat", async (AgentHeartbeatRequest request, CancellationToken cancellationToken) =>
{
    await AppendJsonLineAsync(
        Path.Combine(dataRoot, $"heartbeats-{DateTime.UtcNow:yyyy-MM-dd}.jsonl"),
        request,
        writeGate,
        cancellationToken);

    return Results.Ok(new AgentHeartbeatResponse
    {
        ServerTimeUtc = DateTimeOffset.UtcNow,
        PolicyRefreshRequired = false
    });
});

app.MapPost("/api/v1/agent/file-keys/wrap", (FileKeyWrapRequest request) =>
{
    var kek = GetOrCreateMockKek(dataRoot);
    var plainKey = Convert.FromBase64String(request.PlainKeyBase64);
    if (plainKey.Length != 32) return Results.BadRequest(new { message = "A 256-bit file key is required." });
    try
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plainKey.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(kek, 16);
        aes.Encrypt(nonce, plainKey, ciphertext, tag, request.FileId.ToByteArray());
        var wrapped = new byte[nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, wrapped, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, wrapped, nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, wrapped, nonce.Length + tag.Length, ciphertext.Length);
        return Results.Ok(new FileKeyWrapResponse
        {
            KeyId = "development-mock-kek-v1",
            WrappedKeyBase64 = Convert.ToBase64String(wrapped)
        });
    }
    finally
    {
        CryptographicOperations.ZeroMemory(plainKey);
        CryptographicOperations.ZeroMemory(kek);
    }
});

app.MapPost("/api/v1/agent/file-keys/unwrap", (FileKeyUnwrapRequest request) =>
{
    var kek = GetOrCreateMockKek(dataRoot);
    var wrapped = Convert.FromBase64String(request.WrappedKeyBase64);
    if (wrapped.Length != 60) return Results.BadRequest(new { message = "Invalid wrapped key." });
    var plainKey = new byte[32];
    try
    {
        using var aes = new AesGcm(kek, 16);
        aes.Decrypt(
            wrapped.AsSpan(0, 12),
            wrapped.AsSpan(28, 32),
            wrapped.AsSpan(12, 16),
            plainKey,
            request.FileId.ToByteArray());
        return Results.Ok(new FileKeyUnwrapResponse { PlainKeyBase64 = Convert.ToBase64String(plainKey) });
    }
    catch (CryptographicException)
    {
        return Results.BadRequest(new { message = "Wrapped key authentication failed." });
    }
    finally
    {
        CryptographicOperations.ZeroMemory(plainKey);
        CryptographicOperations.ZeroMemory(wrapped);
        CryptographicOperations.ZeroMemory(kek);
    }
});

app.MapGet("/api/v1/agent/policy", (
    Guid tenantId,
    Guid deviceId,
    long currentVersion) =>
{
    if (!File.Exists(policyPath)) return Results.NotFound(new { message = "Development policy file was not found." });
    var policy = JsonSerializer.Deserialize<DlpPolicy>(File.ReadAllText(policyPath), JsonDefaults.Options);
    if (policy is null) return Results.Problem("Development policy JSON is invalid.");

    policy.Backend.TenantId = tenantId;
    var lastWrite = File.GetLastWriteTimeUtc(policyPath);
    var version = Math.Max(1, new DateTimeOffset(lastWrite).ToUnixTimeMilliseconds());
    if (currentVersion >= version) return Results.NoContent();

    return Results.Ok(new SignedPolicySnapshot
    {
        PolicyId = Guid.Parse("9d8bbcee-e174-49f8-b68a-f81c6f7aa111"),
        Version = version,
        TenantId = tenantId,
        DeviceId = deviceId,
        IssuedAtUtc = DateTimeOffset.UtcNow,
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(7),
        Policy = policy,
        SignatureAlgorithm = "DEVELOPMENT",
        SignatureBase64 = "DEVELOPMENT-UNSIGNED"
    });
});

app.MapGet("/api/v1/development/events", async (int? take, CancellationToken cancellationToken) =>
{
    var maximum = Math.Clamp(take ?? 100, 1, 1000);
    var files = Directory.EnumerateFiles(dataRoot, "security-events-*.jsonl")
        .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
        .ToList();
    var lines = new List<string>();
    foreach (var file in files)
    {
        var fileLines = await File.ReadAllLinesAsync(file, cancellationToken);
        lines.AddRange(fileLines.Reverse());
        if (lines.Count >= maximum) break;
    }

    var events = lines.Take(maximum)
        .Select(line => JsonSerializer.Deserialize<SecurityEventEnvelope>(line, JsonDefaults.Options))
        .Where(item => item is not null)
        .ToList();
    return Results.Ok(events);
});

app.MapPost("/api/v1/development/temporary-permissions", async (
    PermissionGrant grant,
    CancellationToken cancellationToken) =>
{
    if (!File.Exists(policyPath)) return Results.NotFound(new { message = "Development policy file was not found." });
    if (string.IsNullOrWhiteSpace(grant.ActionKey)) return Results.BadRequest(new { message = "actionKey is required." });
    if (grant.ExpiresAtUtc is null || grant.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        return Results.BadRequest(new { message = "A future expiresAtUtc is required for a temporary permission." });

    var policy = JsonSerializer.Deserialize<DlpPolicy>(await File.ReadAllTextAsync(policyPath, cancellationToken), JsonDefaults.Options)
        ?? throw new InvalidOperationException("Development policy JSON is invalid.");
    grant.GrantId = grant.GrantId == Guid.Empty ? Guid.NewGuid() : grant.GrantId;
    grant.Source = PermissionSources.TemporaryGrant;
    grant.CreatedAtUtc = DateTimeOffset.UtcNow;
    policy.Permissions.Grants.RemoveAll(item => item.GrantId == grant.GrantId);
    policy.Permissions.Grants.Add(grant);
    await WritePolicyAtomicallyAsync(policyPath, policy, cancellationToken);
    return Results.Ok(grant);
});

app.MapDelete("/api/v1/development/temporary-permissions/{grantId:guid}", async (
    Guid grantId,
    CancellationToken cancellationToken) =>
{
    if (!File.Exists(policyPath)) return Results.NotFound();
    var policy = JsonSerializer.Deserialize<DlpPolicy>(await File.ReadAllTextAsync(policyPath, cancellationToken), JsonDefaults.Options)
        ?? throw new InvalidOperationException("Development policy JSON is invalid.");
    var grant = policy.Permissions.Grants.FirstOrDefault(item => item.GrantId == grantId);
    if (grant is null) return Results.NotFound();
    grant.RevokedAtUtc = DateTimeOffset.UtcNow;
    grant.RevokedBy = "DevelopmentMockServer";
    await WritePolicyAtomicallyAsync(policyPath, policy, cancellationToken);
    return Results.NoContent();
});

app.Run();

static string ResolveProjectRoot()
{
    var configured = Environment.GetEnvironmentVariable("COMPANY_DLP_PROJECT_ROOT");
    if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);

    var current = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (current is not null)
    {
        if (File.Exists(Path.Combine(current.FullName, "CompanyDlp.sln"))) return current.FullName;
        current = current.Parent;
    }
    throw new InvalidOperationException("COMPANY_DLP_PROJECT_ROOT is required when the solution root cannot be discovered.");
}

static async Task AppendJsonLineAsync<T>(
    string path,
    T value,
    SemaphoreSlim gate,
    CancellationToken cancellationToken)
{
    var line = JsonSerializer.Serialize(value, JsonDefaults.Options).Replace(Environment.NewLine, string.Empty) + Environment.NewLine;
    await gate.WaitAsync(cancellationToken);
    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.AppendAllTextAsync(path, line, cancellationToken);
    }
    finally
    {
        gate.Release();
    }
}

static byte[] GetOrCreateMockKek(string dataRoot)
{
    var path = Path.Combine(dataRoot, "development-mock-kek.bin");
    if (File.Exists(path)) return File.ReadAllBytes(path);
    var key = RandomNumberGenerator.GetBytes(32);
    var temporary = path + ".tmp";
    Directory.CreateDirectory(dataRoot);
    File.WriteAllBytes(temporary, key);
    File.Move(temporary, path, false);
    return key.ToArray();
}

static bool HasValidIntegrityHash(SecurityEventEnvelope value)
{
    var supplied = value.IntegrityHash;
    if (supplied.Length != 64 || !supplied.All(Uri.IsHexDigit)) return false;
    value.IntegrityHash = "";
    try
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonDefaults.Options);
        var expected = SHA256.HashData(bytes);
        var actual = Convert.FromHexString(supplied);
        try { return CryptographicOperations.FixedTimeEquals(expected, actual); }
        finally
        {
            CryptographicOperations.ZeroMemory(expected);
            CryptographicOperations.ZeroMemory(actual);
        }
    }
    finally
    {
        value.IntegrityHash = supplied;
    }
}

static ConcurrentDictionary<Guid, byte> LoadAcceptedEventIds(string dataRoot)
{
    var ids = new ConcurrentDictionary<Guid, byte>();
    try
    {
        foreach (var path in Directory.EnumerateFiles(dataRoot, "security-events-*.jsonl"))
        {
            foreach (var line in File.ReadLines(path))
            {
                try
                {
                    var value = JsonSerializer.Deserialize<SecurityEventEnvelope>(line, JsonDefaults.Options);
                    if (value is not null && value.EventId != Guid.Empty) ids.TryAdd(value.EventId, 0);
                }
                catch { }
            }
        }
    }
    catch { }
    return ids;
}

static async Task WritePolicyAtomicallyAsync(string path, DlpPolicy policy, CancellationToken cancellationToken)
{
    var temporary = path + ".tmp";
    await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(policy, JsonDefaults.Options), cancellationToken);
    File.Move(temporary, path, true);
}
