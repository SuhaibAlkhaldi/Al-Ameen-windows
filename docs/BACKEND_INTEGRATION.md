# Backend Integration Contract

## Zero-rewrite rule

The Windows endpoint can connect to the future backend without changing enforcement code when the backend implements the published OpenAPI contract and preserves schema version `1.0` semantics.

Permitted deployment changes are:

- backend base URL;
- tenant and enrollment values;
- device bearer credential created by enrollment;
- ECDSA policy-signing public key;
- KMS/provider configuration;
- production certificate and trust settings.

Changing endpoint paths, field meanings, permission precedence, signature canonicalization, or accepted decision values requires a new versioned contract.

## Authentication

Development uses `DevelopmentNone` only against the local mock URL. Production uses `DeviceBearerToken`:

1. administrator or deployment workflow provides a one-time enrollment code;
2. endpoint posts device identity to `/api/v1/agent/enroll`;
3. backend returns a short-lived/renewable device access token;
4. endpoint stores it with DPAPI LocalMachine under `%ProgramData%\CompanyDlp\Credentials`;
5. every normal request sends Bearer auth plus tenant/device/agent-version headers.

The production backend should bind the token to tenant/device, rotate it, revoke compromised devices, rate-limit requests, and reject identity mismatches between token, headers, and payload. Heartbeat responses must return authoritative UTC server time; the endpoint uses it to anchor temporary-permission expiry and detect clock rollback.

## Audit ingestion

`POST /api/v1/agent/events/batch` must be idempotent on `(TenantId, EventId)`. Validate:

- authenticated tenant/device match;
- supported protocol/schema versions;
- UTC timestamp bounds;
- required action/event/reason values;
- event integrity hash;
- payload size and detail sanitization.

Return each event in exactly one category: accepted, duplicate, or rejected. A permanent rejection is moved to the endpoint dead-letter queue; a retryable rejection remains pending.

## Policy endpoint

`GET /api/v1/agent/policy` returns 204/304 when no newer policy exists. A returned snapshot must target the authenticated tenant and device, have increasing version, future expiry, and a valid ECDSA-SHA256 signature over the canonical snapshot payload with `SignatureBase64` empty during signing.

## File-key service

The current contract supports key wrapping/unwrapping so the encrypted file contains no plaintext DEK. Production must protect this endpoint with device authentication, authorization for the requested file/tenant/user, TLS, and KMS/HSM-backed KEKs. Plain DEKs must not be logged or persisted by the API.

For stronger architecture, the backend can evolve to a KMS-native envelope operation while preserving a versioned endpoint/provider contract.

## File classification

Current endpoint policy uses provider `BlockAll`. The AI team later implements `/api/v1/agent/file-classification` and returns `FileClassificationResult`. Provider failure is fail-closed. Do not return `IsAllowed=true` without an authenticated, policy-bound classification and a short validity period. Before enabling browser uploads, version an approval-token/preflight flow that binds the allow decision to tenant, device, user, destination, file metadata/hash, policy version, and expiry; the current extension intentionally does not replay a blocked upload.

## Database mapping suggestion

The endpoint does not depend on database tables. The backend may map contracts to `Devices`, `Policies`, `PolicyRules`, `PermissionGrants`, `SecurityEvents`, `FileProtectionTransactions`, and `AgentHeartbeats`. Preserve raw accepted event envelopes or a lossless normalized representation for auditability.
