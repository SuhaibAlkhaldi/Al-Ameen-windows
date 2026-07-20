# Windows Backend Readiness

This Windows endpoint is prepared to connect to a production backend through the versioned agent API contract.

## Production Startup Gate

When `runtime.mode` is `Production`, the service validates configuration before it starts normal protection:

- backend sync is enabled and uses `mode: Production`;
- backend URL is absolute HTTPS and not localhost;
- authentication mode is `DeviceBearerToken`;
- unsigned development policies are disabled;
- an ECDSA policy signing public key is configured;
- the device is enrolled and has a DPAPI-protected bearer token, except during `--enroll`;
- persistent service protection and session-agent supervision are enabled;
- screenshot, screen recording, sensitive-copy, browser upload/drag/drop/paste, USB, software-install, watermark, file protection, and fail-closed file classification settings are enabled according to production requirements;
- risky action defaults are deny unless an explicit valid grant exists.

## Backend Flow

The endpoint never writes directly to the database.

```text
Endpoint action detected
  -> local policy decision
  -> local enforcement
  -> SecurityEventEnvelope
  -> encrypted local outbox
  -> POST /api/v1/agent/events/batch
  -> backend validates and stores in database
```

If the backend is offline, enforcement continues from the last valid local or cached signed policy. Audit events remain encrypted in the outbox and are retried until the backend accepts them or permanently rejects them.

## Required Backend Endpoints

The production backend must implement:

```text
POST /api/v1/agent/enroll
POST /api/v1/agent/heartbeat
GET  /api/v1/agent/policy
POST /api/v1/agent/events/batch
POST /api/v1/agent/file-classification
POST /api/v1/agent/file-keys/wrap
POST /api/v1/agent/file-keys/unwrap
```

The authoritative schema is `contracts/company-dlp-agent-api.openapi.yaml`.

## Audit Event Storage

The backend should persist each `SecurityEventEnvelope` idempotently by `eventId`. Important fields include:

- `tenantId`, `deviceId`, `machineName`;
- `userSid`, `username`, `windowsSessionId`;
- `actionKey`, `eventType`, `decision`, `reasonCode`;
- `policyId`, `policyVersion`, `permissionGrantId`;
- `sourceProcess`;
- `resource`;
- `destination`;
- `details`;
- `occurredAtUtc`, `agentVersion`, `osVersion`;
- `integrityHash`.

Backend batch responses should identify accepted, duplicate, retryable rejected, and permanently rejected events so the endpoint deletes only confirmed events.

## Policy Delivery

Production policy responses must be `SignedPolicySnapshot` values signed with ECDSA-SHA256. The endpoint rejects unsigned production policies, wrong tenant/device snapshots, expired snapshots, future-issued snapshots, invalid signatures, and unsupported signature algorithms.

Temporary permissions must be sent as explicit grants with start and expiry times. The endpoint automatically stops honoring expired grants locally, including when offline.
