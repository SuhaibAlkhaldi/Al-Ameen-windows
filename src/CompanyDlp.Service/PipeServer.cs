using System.IO.Pipes;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using CompanyDlp.Contracts;

namespace CompanyDlp.Service;

public sealed class PipeServer(
    PolicyStore policyStore,
    AgentIdentityProvider identityProvider,
    EffectivePolicyBuilder effectivePolicyBuilder,
    PermissionEvaluator permissionEvaluator,
    ContentClassifier classifier,
    FileClassificationService fileClassificationService,
    AuditLogger auditLogger,
    AuditOutbox auditOutbox,
    FileProtectionCoordinator fileProtectionCoordinator,
    BrowserPolicyManager browserPolicyManager,
    UsbProtectionMonitor usbMonitor,
    RuntimeOverrideStore runtimeOverrides,
    NotificationStore notificationStore,
    ILogger<PipeServer> logger)
{
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _recentMessageIds = new();
    private static readonly TimeSpan MaximumMessageAge = TimeSpan.FromMinutes(5);

    public Task RunAsync(CancellationToken cancellationToken) => AcceptLoopAsync(cancellationToken);

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(cancellationToken);
                _ = HandleClientAsync(pipe, cancellationToken);
                pipe = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                pipe?.Dispose();
                break;
            }
            catch (Exception exception)
            {
                pipe?.Dispose();
                logger.LogError(exception, "Named pipe accept loop failed.");
                await Task.Delay(1000, cancellationToken);
            }
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        var currentUserSid = WindowsIdentity.GetCurrent().User;
        if (currentUserSid is not null)
        {
            security.AddAccessRule(new PipeAccessRule(
                currentUserSid,
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
        }

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            PipeNames.Policy,
            PipeDirection.InOut,
            20,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            64 * 1024,
            64 * 1024,
            security);
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken serverCancellationToken)
    {
        await using (pipe)
        using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true))
        using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true })
        {
            try
            {
                var line = await reader.ReadLineAsync(serverCancellationToken);
                if (string.IsNullOrWhiteSpace(line)) return;

                var request = JsonSerializer.Deserialize<DlpRequest>(line, JsonDefaults.Options);
                DlpResponse response;
                if (request is null)
                {
                    response = DlpResponse.Fail("Invalid request");
                }
                else if (!request.ProtocolVersion.Equals("1.0", StringComparison.Ordinal))
                {
                    response = DlpResponse.Fail("Unsupported IPC protocol version.");
                }
                else if (!TryAcceptMessage(request, out var messageFailure))
                {
                    response = DlpResponse.Fail(messageFailure);
                }
                else
                {
                    var authenticatedClient = CaptureAuthenticatedClient(pipe, request.Context);
                    request.Context = authenticatedClient.Context;
                    var clientAccessToken = authenticatedClient.AccessToken;
                    try
                    {
                        response = await HandleRequestAsync(request, clientAccessToken, serverCancellationToken);
                    }
                    finally
                    {
                        clientAccessToken?.Dispose();
                    }
                }

                await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonDefaults.Options));
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Named pipe client request failed.");
            }
        }
    }


    private bool TryAcceptMessage(DlpRequest request, out string failure)
    {
        failure = "";
        var now = DateTimeOffset.UtcNow;
        if (request.MessageId == Guid.Empty)
        {
            failure = "IPC messageId is required.";
            return false;
        }

        if (request.SentAtUtc == default || (now - request.SentAtUtc).Duration() > MaximumMessageAge)
        {
            failure = "IPC message timestamp is outside the accepted window.";
            return false;
        }

        if (!_recentMessageIds.TryAdd(request.MessageId, now))
        {
            failure = "Duplicate IPC message rejected.";
            return false;
        }

        if (_recentMessageIds.Count > 4096)
        {
            var threshold = now - MaximumMessageAge - TimeSpan.FromMinutes(1);
            foreach (var item in _recentMessageIds)
            {
                if (item.Value < threshold) _recentMessageIds.TryRemove(item.Key, out _);
            }
        }

        return true;
    }

    private async Task<DlpResponse> HandleRequestAsync(
        DlpRequest request,
        SafeAccessTokenHandle? clientAccessToken,
        CancellationToken cancellationToken)
    {
        switch (request.Type)
        {
            case DlpMessageTypes.Ping:
            {
                var policy = policyStore.Get();
                var identity = identityProvider.Get();
                var outboxStatus = auditOutbox.GetStatus();
                return DlpResponse.Ok(data: new ServiceStatus
                {
                    StartedAtUtc = _startedAt,
                    Mode = policy.Runtime.Mode,
                    PolicyPath = policyStore.PolicyPath,
                    EffectiveUsbMode = runtimeOverrides.GetUsbMode(policy.Usb.EnforcementMode),
                    DeviceId = identity.DeviceId,
                    RemotePolicyVersion = policyStore.CurrentRemoteVersion,
                    PendingAuditEventCount = outboxStatus.PendingCount,
                    BackendMode = policy.Backend.Enabled ? policy.Backend.Mode : "Disabled"
                });
            }
            case DlpMessageTypes.GetPolicy:
            {
                var effective = effectivePolicyBuilder.Build(policyStore.Get(), request.Context, DateTimeOffset.UtcNow);
                return DlpResponse.Ok(data: effective);
            }
            case DlpMessageTypes.ReloadPolicy:
            {
                var reloaded = policyStore.Reload();
                return DlpResponse.Ok("Policy reloaded", effectivePolicyBuilder.Build(reloaded, request.Context, DateTimeOffset.UtcNow));
            }
            case DlpMessageTypes.EvaluatePermission:
            {
                var input = request.Data?.Deserialize<PermissionEvaluationRequest>(JsonDefaults.Options)
                    ?? new PermissionEvaluationRequest();
                var decision = permissionEvaluator.Evaluate(
                    policyStore.Get(),
                    input.ActionKey,
                    request.Context,
                    identityProvider.Get(),
                    DateTimeOffset.UtcNow);
                return DlpResponse.Ok(data: decision);
            }
            case DlpMessageTypes.ClassifyText:
            {
                var input = request.Data?.Deserialize<ClassificationRequest>(JsonDefaults.Options) ?? new ClassificationRequest();
                var result = classifier.Classify(input);
                if (result.IsSensitive)
                {
                    await auditLogger.WriteAsync(new AuditEvent
                    {
                        ActionKey = ActionKeys.ClipboardCopySensitive,
                        EventType = "SensitiveClipboardCopyBlocked",
                        Action = input.Channel,
                        Method = input.Channel,
                        Result = "blocked",
                        ReasonCode = "SensitiveContentRuleMatched",
                        RuleId = result.Matches.FirstOrDefault()?.RuleId ?? "",
                        Details = result.FragmentAssemblyDetected ? "Fragment assembly detected" : "Sensitive rule matched"
                    }, request.Context, cancellationToken);
                }
                return DlpResponse.Ok(data: result);
            }
            case DlpMessageTypes.ClassifyFile:
            {
                var input = request.Data?.Deserialize<FileClassificationRequest>(JsonDefaults.Options)
                    ?? new FileClassificationRequest();
                var result = await fileClassificationService.ClassifyAsync(input, request.Context, cancellationToken);
                return DlpResponse.Ok(data: result);
            }
            case DlpMessageTypes.Audit:
            {
                var audit = request.Data?.Deserialize<AuditEvent>(JsonDefaults.Options) ?? new AuditEvent();
                var persisted = await auditLogger.WriteAsync(audit, request.Context, cancellationToken);

                var policy = policyStore.Get();
                if (policy.Notifications.Enabled && BrowserAuditNotificationPolicy.ShouldNotify(audit))
                {
                    var (title, message) = DescribeBlockedBrowserAction(audit.Action);
                    notificationStore.Add(
                        category: "browser",
                        title: title,
                        message: message,
                        severity: "Error",
                        action: audit.Action);
                }

                // Report the real outcome instead of an unconditional "Ok" — callers (e.g. the
                // Desktop hotkey blocker) can only retry a genuinely failed write if they're told it
                // failed.
                return persisted
                    ? DlpResponse.Ok("Audit queued")
                    : DlpResponse.Fail("Audit event could not be persisted");
            }
            case DlpMessageTypes.ProtectFile:
            {
                var input = request.Data?.Deserialize<FileProtectionRequest>(JsonDefaults.Options) ?? new FileProtectionRequest();
                if (clientAccessToken is null || clientAccessToken.IsInvalid)
                    return DlpResponse.Fail("Company DLP could not authenticate the Windows user for this file operation.");

                var result = await WindowsIdentity.RunImpersonatedAsync(
                    clientAccessToken,
                    () => fileProtectionCoordinator.ExecuteAsync(input, request.Context, cancellationToken));
                return result.Success ? DlpResponse.Ok(result.Message, result) : DlpResponse.Fail(result.Message, result);
            }
            case DlpMessageTypes.GetOutboxStatus:
                return DlpResponse.Ok(data: auditOutbox.GetStatus());
            case DlpMessageTypes.ApplyBrowserPolicies:
                await browserPolicyManager.ApplyMachinePoliciesAsync(cancellationToken);
                return DlpResponse.Ok("Machine browser policies applied");
            case DlpMessageTypes.GetUsbSnapshot:
                return DlpResponse.Ok(data: usbMonitor.LastSnapshot);
            case DlpMessageTypes.SetTemporaryUsbBlock:
            {
                if (!policyStore.Get().Runtime.Mode.Equals("Development", StringComparison.OrdinalIgnoreCase))
                    return DlpResponse.Fail("Temporary USB overrides are disabled in Production mode.");
                var data = request.Data?.Deserialize<TemporaryUsbBlockRequest>(JsonDefaults.Options) ?? new TemporaryUsbBlockRequest();
                var minutes = Math.Clamp(data.Minutes, 1, 30);
                runtimeOverrides.EnableTemporaryUsbBlock(TimeSpan.FromMinutes(minutes));
                return DlpResponse.Ok($"USB Block mode enabled temporarily for {minutes} minute(s).");
            }
            case DlpMessageTypes.ResetUsbBaseline:
                if (!policyStore.Get().Runtime.Mode.Equals("Development", StringComparison.OrdinalIgnoreCase))
                    return DlpResponse.Fail("USB baseline reset requires authenticated administration in Production mode.");
                usbMonitor.ResetBaseline();
                return DlpResponse.Ok("USB baseline reset to currently connected devices.");
            case DlpMessageTypes.GetUserNotifications:
            {
                var input = request.Data?.Deserialize<NotificationPollRequest>(JsonDefaults.Options) ?? new NotificationPollRequest();
                return DlpResponse.Ok(data: notificationStore.GetAfter(input.AfterId));
            }
            default:
                return DlpResponse.Fail($"Unknown request type: {request.Type}");
        }
    }

    private static AuthenticatedPipeClient CaptureAuthenticatedClient(NamedPipeServerStream pipe, ClientContext supplied)
    {
        supplied ??= new ClientContext();
        SafeAccessTokenHandle? duplicatedToken = null;
        try
        {
            pipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent(
                    TokenAccessLevels.Query |
                    TokenAccessLevels.Duplicate |
                    TokenAccessLevels.Impersonate);
                supplied.UserSid = identity.User?.Value ?? "";
                supplied.Username = identity.Name ?? "";
                if (!DuplicateTokenEx(
                        identity.AccessToken.DangerousGetHandle(),
                        (uint)(TokenAccessLevels.Query | TokenAccessLevels.Duplicate | TokenAccessLevels.Impersonate),
                        IntPtr.Zero,
                        SecurityImpersonationLevel.Impersonation,
                        TokenType.Impersonation,
                        out duplicatedToken))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not duplicate the named-pipe client token.");
                }
            });
        }
        catch
        {
            duplicatedToken?.Dispose();
            duplicatedToken = null;
            supplied.UserSid = "";
            supplied.Username = pipe.GetImpersonationUserName() ?? supplied.Username;
        }

        supplied.MachineName = Environment.MachineName;
        BindTrustedCallerProcess(pipe, supplied);
        return new AuthenticatedPipeClient(supplied, duplicatedToken);
    }

    private static void BindTrustedCallerProcess(NamedPipeServerStream pipe, ClientContext context)
    {
        context.CallerProcessId = null;
        context.CallerProcessName = "";
        context.CallerProcessPath = "";

        try
        {
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var processId) || processId == 0) return;
            using var process = Process.GetProcessById(checked((int)processId));
            context.CallerProcessId = process.Id;
            context.WindowsSessionId = process.SessionId;
            context.CallerProcessName = process.ProcessName + ".exe";
            try { context.CallerProcessPath = process.MainModule?.FileName ?? ""; } catch { }
        }
        catch
        {
            // The caller may exit between the kernel query and process inspection.
        }
    }

    private sealed record AuthenticatedPipeClient(ClientContext Context, SafeAccessTokenHandle? AccessToken);

    private enum SecurityImpersonationLevel
    {
        Anonymous,
        Identification,
        Impersonation,
        Delegation
    }

    private enum TokenType
    {
        Primary = 1,
        Impersonation = 2
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(
        SafePipeHandle pipe,
        out uint clientProcessId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(
        IntPtr existingToken,
        uint desiredAccess,
        IntPtr tokenAttributes,
        SecurityImpersonationLevel impersonationLevel,
        TokenType tokenType,
        out SafeAccessTokenHandle newToken);

    private static (string Title, string Message) DescribeBlockedBrowserAction(string? action)
    {
        return action?.ToLowerInvariant() switch
        {
            "download" => ("Download blocked", "Downloading files through this browser is not allowed by company security policy."),
            "file-picker" or "showopenfilepicker" or "choosefilesystementries" or "file-input-change" =>
                ("File upload blocked", "Selecting or uploading files through this browser is not allowed by company security policy."),
            "showdirectorypicker" => ("Folder access blocked", "Selecting folders through this browser is not allowed by company security policy."),
            "showsavefilepicker" => ("File operation blocked", "The browser file picker is disabled by company security policy."),
            "file-drag" or "file-drop" =>
                ("File drag and drop blocked", "Dragging or dropping files into browser pages is not allowed by company security policy."),
            "form-file-submit" or "formdata-file" or "formdata-files" or "xhr-file-upload" or "fetch-file-upload" or "beacon-file-upload"
                or "websocket-file-upload" or "rtc-file-upload" =>
                ("File upload blocked", "A browser page attempted to upload a file or binary payload and Company DLP blocked the operation."),
            "web-share-file" => ("File sharing blocked", "Sharing files from this browser is not allowed by company security policy."),
            "worker-file-transfer" => ("File transfer blocked", "A browser page attempted to transfer a file or binary payload to a background worker."),
            "paste-file" or "clipboard-file-transfer" =>
                ("File or image paste blocked", "Pasting or transferring files and images through the browser clipboard is not allowed by company security policy."),
            "copy-sensitive" => ("Sensitive data copy blocked", "The selected content contains sensitive data and cannot be copied."),
            "paste-sensitive" => ("Sensitive data paste blocked", "The pasted content contains sensitive data and cannot be entered into this page."),
            "typed-sensitive" or "input-sensitive" =>
                ("Sensitive data entry blocked", "The entered content contains sensitive data and was blocked by company security policy."),
            "submit-sensitive" => ("Form submission blocked", "The form contains sensitive data and cannot be submitted."),
            _ => ("Browser action blocked", "This browser action is not allowed by company security policy.")
        };
    }
}

