# Windows Ready Architecture

## Trust boundaries

The design separates four trust zones:

1. **LocalSystem Windows Service** owns policy synchronization, permission decisions, protected audit storage, USB/software monitoring, encryption transactions, and production session-agent supervision.
2. **Per-user Session Agent** owns UI-session controls that cannot run correctly from Session 0: clipboard hooks, watermark windows, screenshot hotkeys, recorder notifications, and user interaction.
3. **Browser extension + Browser Bridge + Native Host** enforce browser actions. The extension never writes policy files or audit storage directly. Production uses the signed Native Host, which forwards typed requests over the authenticated named pipe. Development can use an explicit loopback Mock Server fallback when Smart App Control blocks the unsigned Debug host; the Mock Server calls the same Browser Bridge and pipe path.
4. **Backend boundary** is represented during testing by `CompanyDlp.MockServer`; production uses the same versioned HTTP contracts.

## Local data flow

```text
User action
  -> enforcement component detects action
  -> authenticated Windows identity/session context is captured
  -> PermissionEvaluator returns Allow/Block + reason + grant ID
  -> action is enforced locally
  -> SecurityEventFactory creates schema v1 event
  -> SHA-256 integrity hash is added
  -> event is DPAPI-encrypted into the local outbox
  -> AuditSyncWorker sends an idempotent batch
  -> backend returns accepted/duplicate/rejected event IDs
  -> only acknowledged events leave the pending outbox
```

The endpoint remains fail-closed for configured protected actions when the backend is unavailable. Events remain encrypted locally and synchronize after connectivity returns.

## IPC security

`CompanyDlp.Policy.v2` is a local named pipe with ACLs for authenticated local users, SYSTEM, and Administrators. The service does not trust `UserSid` or `Username` supplied in JSON. It impersonates the pipe caller to capture the actual Windows token.

File encryption/decryption requests are executed under the authenticated caller token so the LocalSystem service cannot be abused to access files the caller cannot normally access. IPC messages carry protocol version, message ID, timestamp, client name, session ID, and typed payload. The service rejects stale/replayed message IDs and binds process ID, session ID, process name, and process path from the kernel named-pipe client rather than trusting JSON fields.

## Policy and permission evaluation

Every protected operation has an `ActionKey`. Policy evaluation is local and deterministic:

1. matching emergency deny;
2. highest explicit priority;
3. most specific subject;
4. newest grant;
5. global default when no active grant matches.

A grant is ignored when it has not started, has expired, or has been revoked. Temporary grants therefore expire without backend intervention. Production evaluates temporary grants against a DPAPI-protected trusted clock anchored to backend heartbeat time and monotonic uptime; rollback or unavailable trusted time fails closed. The Permission Lifecycle Monitor produces an auditable expiry event.

Current endpoint-native subjects are User SID, username, device ID, machine name, and global. Department/group resolution belongs in the backend, which should compile group membership into explicit signed endpoint grants or extend schema v2 later.

## Policy synchronization

The endpoint accepts `SignedPolicySnapshot` objects containing policy ID, version, tenant ID, device ID, issued/expiry time, and signature. Production validation requires ECDSA-SHA256. The last accepted snapshot is DPAPI-protected and cached for offline use.

The development mock uses the explicit `DEVELOPMENT-UNSIGNED` marker only when both runtime mode and policy configuration allow it.

## Audit outbox

Each event is stored as an individual DPAPI LocalMachine-protected file to support atomic enqueue, acknowledgement, retries, and dead-letter handling. Production does not require readable JSON audit logs. Development may keep readable diagnostics separately for testers.

`EventId` is globally unique and the batch response distinguishes accepted, duplicate, retryable rejection, and permanent rejection. The mock persists accepted IDs across restarts and validates each event integrity hash.

## Browser protection

The extension observes an explicit user file selection and blocks the `change` event before the page receives it, so filename/type/size metadata can be audited. It also blocks drag/drop, file/image paste, Web Share files, script-triggered pickers without a user gesture, and common page-level File/FormData/fetch/XHR/beacon paths. It records hostname, sanitized destination, action method, and file metadata when available.

The service exposes `IFileClassificationProvider` with `BlockAll` and `AiApi` providers. Current policy remains `BlockAll`; the future AI API implements the fixed classification endpoint and returns a typed result. Browser allow-flow UX must remain fail-closed and should use a preflight/retry approval token rather than replaying untrusted browser events.


### Development browser transport

For managed Chrome/Edge test profiles only, `developmentHttpFallback` may be written to enterprise managed extension storage. If Native Messaging transport cannot start, the extension posts to `http://127.0.0.1:5055/api/v1/development/native-message`; the Mock Server forwards the same typed message through `CompanyDlp.BrowserBridge` to the Service pipe. The setting and profile are restored on test-session exit. Firefox does not use this fallback. Production never sets it and requires a valid signed Native Host.

## File encryption

The Service owns file protection. Explorer/Desktop only send an authenticated request.

Format `CDLPENC2` uses:

- random 256-bit DEK per file;
- AES-256-GCM in authenticated chunks;
- unique nonce construction per chunk;
- authenticated header containing format version, file ID, original name/length, key provider, wrapped key, and chunk size;
- LocalMachine DPAPI for development or backend KMS wrapping for production;
- full decrypt-and-hash verification before plaintext deletion.

The original plaintext is deleted only after encrypted-file authentication and plaintext hash verification succeed. Any error keeps the original.

## Session-agent supervision

In Production, the LocalSystem service enumerates active Windows sessions and ensures one Desktop Session Agent is running per active session. Killing it causes the service to relaunch it and emit an audit event. Development launches the Desktop explicitly and does not enable this supervisor.
