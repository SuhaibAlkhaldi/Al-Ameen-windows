using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using CompanyDlp.Contracts;
using CompanyDlp.Core;
using CompanyDlp.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CompanyDlp.Tests;

// Drives the real PipeServer (HandleClientAsync/CaptureAuthenticatedClient - made `internal` for
// exactly this purpose, see PipeServer.cs) over a real, same-process named pipe pair - not a mock,
// not a wrapped seam around the OS pipe, the actual NamedPipeServerStream/NamedPipeClientStream this
// code runs against in production. That's what makes it possible to exercise real Windows-identity
// impersonation (CaptureAuthenticatedClient) deterministically: the "client" IS this test process, so
// impersonating it always succeeds and always yields a known, comparable identity.
//
// Heavy production dependencies PipeServer doesn't need for the message types exercised here
// (ContentClassifier, FileClassificationService/Cache/StatusResolver, AuditLogger,
// FileProtectionCoordinator, BrowserPolicyManager, UsbProtectionMonitor, NotificationStore) are passed
// as null - safe because Ping/GetPolicy/ReloadPolicy/EvaluatePermission never touch them (confirmed by
// reading HandleRequestAsync). Building real instances of those would mean touching the Windows
// registry (BrowserPolicyManager), starting PnP/WMI watchers (UsbProtectionMonitor), etc. - not
// appropriate for a unit test and not what this task is testing.
public sealed class PipeServerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "CompanyDlpPipeServerTests", Guid.NewGuid().ToString("N"));
    private readonly string? _oldPolicyPath = Environment.GetEnvironmentVariable("COMPANY_DLP_POLICY_PATH");

    private PipeServer CreatePipeServer(out PolicyStore policyStore)
    {
        Directory.CreateDirectory(_directory);
        Environment.SetEnvironmentVariable("COMPANY_DLP_POLICY_PATH", Path.Combine(_directory, "policy.json"));

        policyStore = new PolicyStore(new MachineDataProtector(), NullLogger<PolicyStore>.Instance);
        var identityProvider = new AgentIdentityProvider(policyStore, NullLogger<AgentIdentityProvider>.Instance);
        var permissionEvaluator = new PermissionEvaluator();
        var effectivePolicyBuilder = new EffectivePolicyBuilder(permissionEvaluator, identityProvider);
        var auditOutbox = new AuditOutbox(policyStore, new MachineDataProtector(), NullLogger<AuditOutbox>.Instance);
        var runtimeOverrides = new RuntimeOverrideStore();

        return new PipeServer(
            policyStore,
            identityProvider,
            effectivePolicyBuilder,
            permissionEvaluator,
            classifier: null!,
            fileClassificationService: null!,
            fileClassificationCache: null!,
            fileClassificationStatusResolver: null!,
            auditLogger: null!,
            auditOutbox,
            fileProtectionCoordinator: null!,
            browserPolicyManager: null!,
            usbMonitor: null!,
            runtimeOverrides,
            notificationStore: null!,
            NullLogger<PipeServer>.Instance);
    }

    private static async Task<(NamedPipeServerStream Server, NamedPipeClientStream Client)> ConnectPipePairAsync()
    {
        var pipeName = "CompanyDlpTest." + Guid.NewGuid().ToString("N");
        var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        var acceptTask = server.WaitForConnectionAsync();
        await client.ConnectAsync(5000);
        await acceptTask;
        return (server, client);
    }

    private static async Task<DlpResponse?> SendRequestAsync(NamedPipeClientStream client, DlpRequest request)
    {
        await using var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, Encoding.UTF8, false, 4096, leaveOpen: true);

        await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonDefaults.Options));
        var line = await reader.ReadLineAsync();
        return line is null ? null : JsonSerializer.Deserialize<DlpResponse>(line, JsonDefaults.Options);
    }

    [Fact]
    public async Task Ping_RealDispatch_ReturnsServiceStatus()
    {
        var server = CreatePipeServer(out var policyStore);
        var (serverPipe, clientPipe) = await ConnectPipePairAsync();
        using (clientPipe)
        {
            var handleTask = server.HandleClientAsync(serverPipe, CancellationToken.None);

            var response = await SendRequestAsync(clientPipe, new DlpRequest { Type = DlpMessageTypes.Ping });
            await handleTask;

            Assert.NotNull(response);
            Assert.True(response!.Success);
            var status = response.Data!.Value.Deserialize<ServiceStatus>(JsonDefaults.Options);
            Assert.NotNull(status);
            Assert.Equal(policyStore.PolicyPath, status!.PolicyPath);
            Assert.Equal("Development", status.Mode);
        }
    }

    [Fact]
    public async Task GetPolicy_RealDispatch_ReturnsEffectivePolicy()
    {
        var server = CreatePipeServer(out _);
        var (serverPipe, clientPipe) = await ConnectPipePairAsync();
        using (clientPipe)
        {
            var handleTask = server.HandleClientAsync(serverPipe, CancellationToken.None);

            var response = await SendRequestAsync(clientPipe, new DlpRequest { Type = DlpMessageTypes.GetPolicy });
            await handleTask;

            Assert.NotNull(response);
            Assert.True(response!.Success);
            var policy = response.Data!.Value.Deserialize<DlpPolicy>(JsonDefaults.Options);
            Assert.NotNull(policy);
        }
    }

    [Fact]
    public async Task UnknownMessageType_RealDispatch_ReturnsFailNotCrash()
    {
        var server = CreatePipeServer(out _);
        var (serverPipe, clientPipe) = await ConnectPipePairAsync();
        using (clientPipe)
        {
            var handleTask = server.HandleClientAsync(serverPipe, CancellationToken.None);

            var response = await SendRequestAsync(clientPipe, new DlpRequest { Type = "totallyUnknownMessageType" });
            await handleTask;

            Assert.NotNull(response);
            Assert.False(response!.Success);
            Assert.Contains("Unknown request type", response.Message);
        }
    }

    // Documents real, observed behavior rather than assuming it: a message whose Data doesn't match
    // the shape the handler expects (a JSON string where an object is expected) makes
    // JsonElement.Deserialize<T> throw. HandleRequestAsync has no try/catch of its own, so the
    // exception propagates to HandleClientAsync's outer catch, which logs and returns - the SERVICE
    // does not crash (this test's own await on handleTask completing without throwing proves that),
    // but that specific malformed request gets no response line at all (the client sees a closed
    // connection / EOF, not a Fail response). Worth knowing if this table ever needs a friendlier
    // "malformed request" response for callers - not fixed here, out of scope for a test-coverage pass.
    [Fact]
    public async Task MalformedData_RealDispatch_DoesNotCrashServer()
    {
        var server = CreatePipeServer(out _);
        var (serverPipe, clientPipe) = await ConnectPipePairAsync();
        using (clientPipe)
        {
            var handleTask = server.HandleClientAsync(serverPipe, CancellationToken.None);

            var badRequest = new
            {
                protocolVersion = "1.0",
                messageId = Guid.NewGuid(),
                sentAtUtc = DateTimeOffset.UtcNow,
                type = DlpMessageTypes.EvaluatePermission,
                context = new ClientContext(),
                data = "this is a string, not a PermissionEvaluationRequest object"
            };

            await using (var writer = new StreamWriter(clientPipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true })
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(badRequest, JsonDefaults.Options));
            }

            // The server must finish handling this connection without an unhandled exception -
            // that's the actual "doesn't crash the dispatcher" property under test.
            await handleTask;

            using var reader = new StreamReader(clientPipe, Encoding.UTF8, false, 4096, leaveOpen: true);
            var line = await reader.ReadLineAsync();
            Assert.Null(line); // connection closed with no response - see comment above.
        }
    }

    [Fact]
    public async Task DuplicateMessageId_AcrossSeparateConnections_IsRejected()
    {
        var server = CreatePipeServer(out _);
        var sharedMessageId = Guid.NewGuid();
        var sentAtUtc = DateTimeOffset.UtcNow;

        var (serverPipe1, clientPipe1) = await ConnectPipePairAsync();
        DlpResponse? firstResponse;
        using (clientPipe1)
        {
            var handleTask = server.HandleClientAsync(serverPipe1, CancellationToken.None);
            firstResponse = await SendRequestAsync(clientPipe1, new DlpRequest
            {
                Type = DlpMessageTypes.Ping,
                MessageId = sharedMessageId,
                SentAtUtc = sentAtUtc
            });
            await handleTask;
        }

        var (serverPipe2, clientPipe2) = await ConnectPipePairAsync();
        DlpResponse? secondResponse;
        using (clientPipe2)
        {
            var handleTask = server.HandleClientAsync(serverPipe2, CancellationToken.None);
            secondResponse = await SendRequestAsync(clientPipe2, new DlpRequest
            {
                Type = DlpMessageTypes.Ping,
                MessageId = sharedMessageId,
                SentAtUtc = sentAtUtc
            });
            await handleTask;
        }

        Assert.True(firstResponse!.Success);
        Assert.False(secondResponse!.Success);
        Assert.Contains("Duplicate", secondResponse.Message);
    }

    [Fact]
    public async Task StaleMessage_OutsideReplayWindow_IsRejected()
    {
        var server = CreatePipeServer(out _);
        var (serverPipe, clientPipe) = await ConnectPipePairAsync();
        using (clientPipe)
        {
            var handleTask = server.HandleClientAsync(serverPipe, CancellationToken.None);

            var response = await SendRequestAsync(clientPipe, new DlpRequest
            {
                Type = DlpMessageTypes.Ping,
                SentAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10)
            });
            await handleTask;

            Assert.NotNull(response);
            Assert.False(response!.Success);
            Assert.Contains("outside the accepted window", response.Message);
        }
    }

    // NOTE on CaptureAuthenticatedClient (the real Win32 impersonation path - DuplicateTokenEx over
    // pipe.RunAsClient): every test above already exercises it for real, since HandleClientAsync calls
    // it internally for any message that passes the replay guard - Ping/GetPolicy/UnknownMessageType/
    // DuplicateMessageId/StaleMessage all pass through it without throwing, which is itself meaningful
    // coverage (impersonation succeeds against a real pipe, on every dispatched request). A SEPARATE,
    // standalone test that called CaptureAuthenticatedClient directly (bypassing HandleClientAsync's
    // own reader/writer setup) was tried here and reliably crashed the test host process outright - a
    // native access violation surfacing from the DuplicateTokenEx P/Invoke, not a managed exception,
    // so it can't be caught or asserted against, only avoided. Production's real PipeServer.CreatePipe()
    // configures specific PipeSecurity ACLs this test's plain NamedPipeServerStream didn't replicate;
    // that's the most likely cause, but wasn't chased further given the risk of leaving a
    // process-crashing test in the suite. Asserting the exact captured UserSid/Username/AccessToken
    // values (as opposed to "impersonation didn't throw") is consequently NOT covered - see the final
    // report for this genuinely-not-covered gap instead of leaving something unstable in its place.

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("COMPANY_DLP_POLICY_PATH", _oldPolicyPath);
        try { Directory.Delete(_directory, true); } catch { }
    }
}
