using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CompanyDlp.Contracts;

namespace CompanyDlp.Service;

public sealed class SecurityEventFactory(
    PolicyStore policyStore,
    AgentIdentityProvider identityProvider)
{
    public SecurityEventEnvelope Create(AuditEvent audit, ClientContext? requestContext = null)
    {
        ArgumentNullException.ThrowIfNull(audit);
        var identity = identityProvider.Get();
        var policy = policyStore.Get();
        var context = requestContext ?? new ClientContext
        {
            UserSid = audit.UserSid,
            Username = audit.Username,
            MachineName = audit.MachineName,
            WindowsSessionId = audit.WindowsSessionId,
            ClientName = "service"
        };

        var envelope = new SecurityEventEnvelope
        {
            EventId = audit.EventId == Guid.Empty ? Guid.NewGuid() : audit.EventId,
            CorrelationId = audit.CorrelationId == Guid.Empty ? Guid.NewGuid() : audit.CorrelationId,
            TenantId = identity.TenantId,
            DeviceId = identity.DeviceId,
            UserSid = Sanitize(context.UserSid, 200),
            Username = Sanitize(string.IsNullOrWhiteSpace(context.Username) ? audit.Username : context.Username, 256),
            MachineName = Sanitize(string.IsNullOrWhiteSpace(context.MachineName) ? identity.MachineName : context.MachineName, 256),
            WindowsSessionId = context.WindowsSessionId,
            ActionKey = string.IsNullOrWhiteSpace(audit.ActionKey) ? ResolveActionKey(audit) : audit.ActionKey,
            EventType = Sanitize(audit.EventType, 100),
            Decision = ResolveDecision(audit.Result),
            ReasonCode = string.IsNullOrWhiteSpace(audit.ReasonCode) ? ResolveReasonCode(audit) : Sanitize(audit.ReasonCode, 150),
            PolicyId = policyStore.CurrentRemotePolicyId,
            PolicyVersion = policyStore.CurrentRemoteVersion > 0 ? policyStore.CurrentRemoteVersion : null,
            RuleId = Sanitize(audit.RuleId, 150),
            PermissionGrantId = audit.PermissionGrantId,
            SourceProcess = BuildProcessContext(audit),
            Resource = BuildResourceContext(audit),
            Destination = string.IsNullOrWhiteSpace(audit.Destination)
                ? null
                : new DestinationContext { Type = "Destination", Value = SanitizeDestination(audit.Destination) },
            Details = JsonSerializer.SerializeToElement(new
            {
                action = Sanitize(audit.Action, 150),
                method = Sanitize(audit.Method, 150),
                details = Sanitize(audit.Details, 2000),
                deviceInstanceId = Sanitize(audit.DeviceInstanceId, 1000),
                ipcClient = new
                {
                    declaredName = Sanitize(context.ClientName, 150),
                    declaredVersion = Sanitize(context.ClientVersion, 50),
                    callerProcessId = context.CallerProcessId,
                    callerProcessName = Sanitize(context.CallerProcessName, 260),
                    callerProcessPath = Sanitize(context.CallerProcessPath, 1000)
                }
            }, JsonDefaults.Options),
            OccurredAtUtc = audit.OccurredAtUtc == default ? DateTimeOffset.UtcNow : audit.OccurredAtUtc,
            AgentVersion = identity.AgentVersion,
            OsVersion = Environment.OSVersion.VersionString,
            IsDevelopmentEvent = policy.Runtime.Mode.Equals("Development", StringComparison.OrdinalIgnoreCase)
        };

        envelope.IntegrityHash = ComputeIntegrityHash(envelope);
        return envelope;
    }

    private static ProcessContext? BuildProcessContext(AuditEvent audit)
    {
        if (string.IsNullOrWhiteSpace(audit.SourceProcessName)
            && string.IsNullOrWhiteSpace(audit.SourceProcessPath)
            && audit.SourceProcessId is null)
            return null;

        return new ProcessContext
        {
            Name = Sanitize(audit.SourceProcessName, 260),
            Path = Sanitize(audit.SourceProcessPath, 1000),
            ProcessId = audit.SourceProcessId,
            Publisher = Sanitize(audit.SourceProcessPublisher, 500),
            Sha256 = NormalizeSha256(audit.SourceProcessSha256)
        };
    }

    private static ResourceContext? BuildResourceContext(AuditEvent audit)
    {
        if (string.IsNullOrWhiteSpace(audit.ResourceName)
            && string.IsNullOrWhiteSpace(audit.ResourceExtension)
            && audit.ResourceSizeBytes is null)
            return null;

        return new ResourceContext
        {
            Type = "File",
            Name = Sanitize(Path.GetFileName(audit.ResourceName), 500),
            Extension = Sanitize(audit.ResourceExtension, 30),
            SizeBytes = audit.ResourceSizeBytes,
            Sha256 = NormalizeSha256(audit.ResourceSha256)
        };
    }

    private static string ResolveActionKey(AuditEvent audit)
    {
        var eventType = audit.EventType?.ToLowerInvariant() ?? "";
        var action = audit.Action?.ToLowerInvariant() ?? "";

        if (eventType.Contains("screen-recorder")) return ActionKeys.ScreenRecording;
        if (eventType.Contains("screen-capture")) return ActionKeys.ScreenCapture;
        if (eventType.Contains("sensitive") || eventType.Contains("clipboard")) return ActionKeys.ClipboardCopySensitive;
        if (eventType.Contains("usb")) return ActionKeys.UsbDeviceConnect;
        if (eventType.Contains("software") || eventType.Contains("install")) return ActionKeys.SoftwareInstall;
        if (eventType.Contains("file-crypto") && action.Contains("decrypt")) return ActionKeys.FileDecrypt;
        if (eventType.Contains("file-crypto")) return ActionKeys.FileEncrypt;
        if (eventType.Contains("browser"))
        {
            if (action.Contains("drop") || action.Contains("drag")) return ActionKeys.BrowserDragDrop;
            if (action.Contains("paste") && action.Contains("image")) return ActionKeys.BrowserImagePaste;
            if (action.Contains("paste")) return ActionKeys.BrowserFilePaste;
            return ActionKeys.BrowserUpload;
        }
        return string.IsNullOrWhiteSpace(action) ? "unknown" : action;
    }

    private static EnforcementDecision ResolveDecision(string? result)
    {
        var normalized = result?.ToLowerInvariant() ?? "";
        if (normalized.Contains("block") || normalized.Contains("terminated")) return EnforcementDecision.Block;
        if (normalized.Contains("allow") || normalized.Contains("success") || normalized.Contains("succeeded")) return EnforcementDecision.Allow;
        if (normalized.Contains("fail") || normalized.Contains("error")) return EnforcementDecision.Error;
        return EnforcementDecision.Audit;
    }

    private static string ResolveReasonCode(AuditEvent audit)
    {
        var result = audit.Result?.ToLowerInvariant() ?? "";
        if (result.Contains("block") || result.Contains("terminated")) return "DeniedByPolicy";
        if (result.Contains("fail")) return "EnforcementFailed";
        if (result.Contains("success") || result.Contains("succeeded")) return "OperationSucceeded";
        return "AuditOnly";
    }

    private static string ComputeIntegrityHash(SecurityEventEnvelope envelope)
    {
        var previous = envelope.IntegrityHash;
        envelope.IntegrityHash = "";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonDefaults.Options);
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        finally
        {
            envelope.IntegrityHash = previous;
        }
    }

    private static string SanitizeDestination(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}"[..Math.Min($"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}".Length, 1000)];
        return Sanitize(value, 1000);
    }

    private static string NormalizeSha256(string? value)
    {
        var normalized = (value ?? "").Trim().ToUpperInvariant();
        return normalized.Length == 64 && normalized.All(Uri.IsHexDigit) ? normalized : "";
    }

    private static string Sanitize(string? value, int maximumLength)
    {
        var cleaned = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return cleaned.Length <= maximumLength ? cleaned : cleaned[..maximumLength];
    }
}
