using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using CompanyDlp.AdminApi.Configuration;
using CompanyDlp.AdminApi.Data;
using CompanyDlp.AdminApi.Domain;
using CompanyDlp.AdminApi.Security;
using CompanyDlp.AdminApi.Services;
using CompanyDlp.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CompanyDlp.AdminApi.Endpoints;

public static class AgentEndpoints
{
    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/agent");
        group.MapPost("/enroll", EnrollAsync).RequireRateLimiting("Enrollment");
        group.MapPost("/heartbeat", HeartbeatAsync);
        group.MapGet("/policy", GetPolicyAsync);
        group.MapPost("/events/batch", IngestEventsAsync);
        group.MapPost("/file-classification", ClassifyFile);
        group.MapPost("/file-keys/wrap", WrapFileKeyAsync);
        group.MapPost("/file-keys/unwrap", UnwrapFileKeyAsync);
        return endpoints;
    }

    private static async Task<IResult> EnrollAsync(
        AgentEnrollmentRequest request,
        CompanyDlpDbContext db,
        IOptions<AgentSecurityOptions> securityOptions,
        AdminAuditWriter auditWriter,
        CancellationToken ct)
    {
        if (request.TenantId == Guid.Empty || request.DeviceId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.MachineName)
            || request.MachineName.Trim().Length > 256
            || (request.AgentVersion ?? "").Trim().Length > 50
            || string.IsNullOrWhiteSpace(request.EnrollmentCode)
            || request.EnrollmentCode.Length > 512)
            return Results.BadRequest(new { error = "InvalidEnrollmentRequest" });

        var now = DateTimeOffset.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var codeHash = TokenUtilities.HashToken(request.EnrollmentCode);
        var code = await db.EnrollmentCodes.SingleOrDefaultAsync(
            value => value.TenantId == request.TenantId && value.CodeHashHex == codeHash,
            ct);
        if (code is null || code.UsedAtUtc is not null || code.ExpiresAtUtc <= now)
            return Results.BadRequest(new { error = "InvalidExpiredOrUsedEnrollmentCode" });
        if (!await db.Tenants.AnyAsync(value => value.Id == request.TenantId && value.IsActive, ct))
            return Results.BadRequest(new { error = "TenantNotActive" });

        var existing = await db.Devices.SingleOrDefaultAsync(value => value.Id == request.DeviceId, ct);
        if (existing is not null && existing.TenantId != request.TenantId)
            return Results.Conflict(new { error = "DeviceIdBelongsToAnotherTenant" });

        var token = TokenUtilities.CreateOpaqueToken();
        var expires = now.AddDays(Math.Clamp(securityOptions.Value.DeviceTokenDays, 1, 365));
        var device = existing ?? new DeviceEntity
        {
            Id = request.DeviceId,
            TenantId = request.TenantId,
            EnrolledAtUtc = now
        };
        device.MachineName = request.MachineName.Trim();
        device.AgentVersion = (request.AgentVersion ?? "").Trim();
        device.TokenHashHex = TokenUtilities.HashToken(token);
        device.TokenExpiresAtUtc = expires;
        device.IsActive = true;
        device.LastSeenAtUtc = now;
        if (existing is null) db.Devices.Add(device);
        code.UsedAtUtc = now;
        await db.SaveChangesAsync(ct);
        await auditWriter.WriteAsync(
            request.TenantId,
            existing is null ? "DeviceEnrolled" : "DeviceReenrolled",
            "Device",
            device.Id.ToString("D"),
            new { device.MachineName, device.AgentVersion, TokenExpiresAtUtc = expires },
            ct);
        await transaction.CommitAsync(ct);

        return Results.Ok(new AgentEnrollmentResponse
        {
            AccessToken = token,
            ExpiresAtUtc = expires
        });
    }

    private static async Task<IResult> HeartbeatAsync(
        AgentHeartbeatRequest request,
        HttpContext http,
        CompanyDlpDbContext db,
        CancellationToken ct)
    {
        var agent = http.GetAgentContext();
        if (request.TenantId != agent.TenantId || request.DeviceId != agent.DeviceId)
            return Results.BadRequest(new { error = "AgentIdentityMismatch" });
        if (string.IsNullOrWhiteSpace(request.MachineName)
            || request.MachineName.Trim().Length > 256
            || (request.AgentVersion ?? "").Trim().Length > 50
            || (request.OsVersion ?? "").Trim().Length > 500
            || request.LastAppliedPolicyVersion < 0)
            return Results.BadRequest(new { error = "InvalidHeartbeatRequest" });

        var device = await db.Devices.SingleAsync(value => value.Id == agent.DeviceId && value.TenantId == agent.TenantId, ct);
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(value => value.Id == agent.TenantId, ct);
        device.MachineName = request.MachineName.Trim();
        device.AgentVersion = (request.AgentVersion ?? "").Trim();
        device.OsVersion = (request.OsVersion ?? "").Trim();
        device.LastSeenAtUtc = DateTimeOffset.UtcNow;
        device.LastAppliedPolicyVersion = request.LastAppliedPolicyVersion;
        device.PendingAuditEventCount = Math.Max(0, request.PendingAuditEventCount);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new AgentHeartbeatResponse
        {
            ServerTimeUtc = DateTimeOffset.UtcNow,
            PolicyRefreshRequired = request.LastAppliedPolicyVersion < tenant.PolicyRevision
        });
    }

    private static async Task<IResult> GetPolicyAsync(
        Guid tenantId,
        Guid deviceId,
        long currentVersion,
        HttpContext http,
        CompanyDlpDbContext db,
        PolicyCompiler compiler,
        CancellationToken ct)
    {
        var agent = http.GetAgentContext();
        if (tenantId != agent.TenantId || deviceId != agent.DeviceId)
            return Results.BadRequest(new { error = "AgentIdentityMismatch" });
        var revision = await db.Tenants.AsNoTracking()
            .Where(value => value.Id == tenantId && value.IsActive)
            .Select(value => value.PolicyRevision)
            .SingleAsync(ct);
        if (currentVersion >= revision) return Results.NoContent();
        return Results.Ok(await compiler.BuildSnapshotAsync(tenantId, deviceId, ct));
    }

    private static async Task<IResult> IngestEventsAsync(
        AuditBatchRequest request,
        HttpContext http,
        CompanyDlpDbContext db,
        AuditIntegrityValidator validator,
        CancellationToken ct)
    {
        var agent = http.GetAgentContext();
        if (request.TenantId != agent.TenantId || request.DeviceId != agent.DeviceId)
            return Results.BadRequest(new { error = "AgentIdentityMismatch" });
        if (request.Events is null)
            return Results.BadRequest(new { error = "AuditEventsRequired" });
        if (request.Events.Count > 500)
            return Results.BadRequest(new { error = "AuditBatchTooLarge" });
        if ((request.AgentVersion ?? "").Trim().Length > 50)
            return Results.BadRequest(new { error = "AgentVersionTooLong" });

        var response = new AuditBatchResponse();
        var candidateIds = request.Events
            .Where(value => value is not null && value.EventId != Guid.Empty)
            .Select(value => value!.EventId)
            .Distinct()
            .ToList();
        var duplicateIds = (await db.SecurityEvents.AsNoTracking()
            .Where(value => value.TenantId == agent.TenantId && candidateIds.Contains(value.EventId))
            .Select(value => value.EventId)
            .ToListAsync(ct))
            .ToHashSet();
        var seenInBatch = new HashSet<Guid>();
        var now = DateTimeOffset.UtcNow;

        foreach (var securityEvent in request.Events)
        {
            if (securityEvent is null)
            {
                response.RejectedEvents.Add(new RejectedAuditEvent
                {
                    EventId = Guid.Empty,
                    ReasonCode = "NullAuditEvent",
                    Retryable = false
                });
                continue;
            }
            var rejection = ValidateEvent(securityEvent, agent, validator, now);
            if (rejection is not null)
            {
                response.RejectedEvents.Add(new RejectedAuditEvent
                {
                    EventId = securityEvent.EventId,
                    ReasonCode = rejection,
                    Retryable = false
                });
                continue;
            }

            var payloadJson = JsonSerializer.Serialize(securityEvent, JsonDefaults.Options);
            if (System.Text.Encoding.UTF8.GetByteCount(payloadJson) > 65_536)
            {
                response.RejectedEvents.Add(new RejectedAuditEvent
                {
                    EventId = securityEvent.EventId,
                    ReasonCode = "AuditEventPayloadTooLarge",
                    Retryable = false
                });
                continue;
            }

            if (duplicateIds.Contains(securityEvent.EventId) || !seenInBatch.Add(securityEvent.EventId))
            {
                response.DuplicateEventIds.Add(securityEvent.EventId);
                continue;
            }

            db.SecurityEvents.Add(new SecurityEventEntity
            {
                TenantId = agent.TenantId,
                EventId = securityEvent.EventId,
                DeviceId = agent.DeviceId,
                CorrelationId = securityEvent.CorrelationId,
                UserId = securityEvent.UserId,
                ActionKey = securityEvent.ActionKey,
                EventType = securityEvent.EventType,
                Decision = securityEvent.Decision.ToString(),
                ReasonCode = securityEvent.ReasonCode ?? "",
                OccurredAtUtc = securityEvent.OccurredAtUtc,
                ReceivedAtUtc = now,
                PayloadJson = payloadJson
            });
            response.AcceptedEventIds.Add(securityEvent.EventId);
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(response);
    }

    private static IResult ClassifyFile(FileClassificationRequest request, HttpContext http)
    {
        var agent = http.GetAgentContext();
        if (request.TenantId != agent.TenantId || request.DeviceId != agent.DeviceId)
            return Results.BadRequest(new { error = "AgentIdentityMismatch" });
        if (request.RequestId == Guid.Empty || request.SizeBytes < 0
            || (request.FileName ?? "").Length > 500
            || (request.Extension ?? "").Length > 100
            || (request.MimeType ?? "").Length > 200
            || (request.Sha256 ?? "").Length > 128
            || (request.Channel ?? "").Length > 200
            || (request.Destination ?? "").Length > 2000)
            return Results.BadRequest(new { error = "InvalidFileClassificationRequest" });
        return Results.Ok(new FileClassificationResult
        {
            RequestId = request.RequestId,
            IsAllowed = false,
            IsSensitive = true,
            Classification = "Sensitive",
            ReasonCode = "CentralBlockAllUntilAiProviderEnabled",
            Provider = FileClassificationProviders.BlockAll,
            EvaluatedAtUtc = DateTimeOffset.UtcNow
        });
    }

    private static async Task<IResult> WrapFileKeyAsync(
        FileKeyWrapRequest request,
        HttpContext http,
        FileKeyEnvelopeService envelopeService,
        DevicePermissionAuthorizer permissionAuthorizer,
        CancellationToken ct)
    {
        var agent = http.GetAgentContext();
        if (request.TenantId != agent.TenantId || request.DeviceId != agent.DeviceId || request.FileId == Guid.Empty)
            return Results.BadRequest(new { error = "AgentIdentityMismatch" });
        if (string.IsNullOrWhiteSpace(request.PlainKeyBase64) || request.PlainKeyBase64.Length > 256)
            return Results.BadRequest(new { error = "InvalidPlainKeyBase64" });
        var permission = await permissionAuthorizer.EvaluateAsync(agent.TenantId, agent.DeviceId, ActionKeys.FileEncrypt, ct);
        if (!permission.IsAllowed)
            return Results.Json(new { error = "FileEncryptDenied", permission.ReasonCode, permission.PermissionGrantId }, statusCode: StatusCodes.Status403Forbidden);

        byte[] plainKey;
        try { plainKey = Convert.FromBase64String(request.PlainKeyBase64); }
        catch (FormatException) { return Results.BadRequest(new { error = "InvalidPlainKeyBase64" }); }
        try
        {
            if (plainKey.Length != 32) return Results.BadRequest(new { error = "A256BitFileKeyIsRequired" });
            var wrapped = envelopeService.Wrap(agent.TenantId, request.FileId, plainKey);
            return Results.Ok(new FileKeyWrapResponse
            {
                KeyId = wrapped.KeyId,
                WrappedKeyBase64 = wrapped.WrappedKeyBase64
            });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainKey);
        }
    }

    private static async Task<IResult> UnwrapFileKeyAsync(
        FileKeyUnwrapRequest request,
        HttpContext http,
        FileKeyEnvelopeService envelopeService,
        DevicePermissionAuthorizer permissionAuthorizer,
        CancellationToken ct)
    {
        var agent = http.GetAgentContext();
        if (request.TenantId != agent.TenantId || request.DeviceId != agent.DeviceId || request.FileId == Guid.Empty)
            return Results.BadRequest(new { error = "AgentIdentityMismatch" });
        if (string.IsNullOrWhiteSpace(request.KeyId) || request.KeyId.Length > 200
            || string.IsNullOrWhiteSpace(request.WrappedKeyBase64) || request.WrappedKeyBase64.Length > 8192)
            return Results.BadRequest(new { error = "InvalidWrappedFileKey" });
        var permission = await permissionAuthorizer.EvaluateAsync(agent.TenantId, agent.DeviceId, ActionKeys.FileDecrypt, ct);
        if (!permission.IsAllowed)
            return Results.Json(new { error = "FileDecryptDenied", permission.ReasonCode, permission.PermissionGrantId }, statusCode: StatusCodes.Status403Forbidden);

        byte[] plainKey;
        try { plainKey = envelopeService.Unwrap(agent.TenantId, request.FileId, request.KeyId, request.WrappedKeyBase64); }
        catch (Exception exception) when (exception is FormatException or System.Security.Cryptography.CryptographicException or InvalidOperationException)
        {
            return Results.BadRequest(new { error = "InvalidWrappedFileKey" });
        }
        try
        {
            if (plainKey.Length != 32) return Results.BadRequest(new { error = "InvalidUnwrappedFileKey" });
            return Results.Ok(new FileKeyUnwrapResponse { PlainKeyBase64 = Convert.ToBase64String(plainKey) });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainKey);
        }
    }

    private static string? ValidateEvent(
        SecurityEventEnvelope securityEvent,
        AgentRequestContext agent,
        AuditIntegrityValidator validator,
        DateTimeOffset now)
    {
        if (securityEvent.EventId == Guid.Empty || securityEvent.CorrelationId == Guid.Empty)
            return "InvalidEventIdentity";
        if (securityEvent.TenantId != agent.TenantId || securityEvent.DeviceId != agent.DeviceId)
            return "InvalidEventIdentity";
        if (!string.Equals(securityEvent.ProtocolVersion, "1.0", StringComparison.Ordinal)
            || !string.Equals(securityEvent.EventSchemaVersion, "1.0", StringComparison.Ordinal))
            return "UnsupportedEventSchemaVersion";
        if (string.IsNullOrWhiteSpace(securityEvent.ActionKey) || string.IsNullOrWhiteSpace(securityEvent.EventType))
            return "MissingEventFields";
        if (!Enum.IsDefined(securityEvent.Decision)) return "InvalidDecision";
        if (securityEvent.ActionKey.Length > 200 || securityEvent.EventType.Length > 200
            || (securityEvent.ReasonCode ?? "").Length > 200)
            return "EventFieldTooLong";
        if (securityEvent.OccurredAtUtc > now.AddMinutes(10) || securityEvent.OccurredAtUtc < now.AddDays(-30))
            return "EventTimestampOutOfRange";
        if (!validator.IsValid(securityEvent)) return "IntegrityHashMismatch";
        return null;
    }
}

