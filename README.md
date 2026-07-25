# Company DLP v1.1.0 — Windows Endpoint + Central Administration

This source package contains the Windows DLP endpoint and a complete central-management vertical slice:

```text
Angular Admin Portal
        ↓ JWT/RBAC
ASP.NET Core Admin API
        ↓ EF Core
Microsoft SQL Server
        ↓ signed, device-targeted policy
Windows Service + Desktop Agent + Browser Bridge
```

Administrators change permissions centrally. The API stores the decision, increments the tenant policy revision, and compiles a snapshot only for the target device. The Windows agent learns about the new revision through heartbeat, downloads the snapshot, verifies its identity/version/expiry/signature, and applies the existing fail-closed `PermissionEvaluator`.

## Included projects

- `CompanyDlp.AdminApi`: ASP.NET Core API, EF Core SQL Server persistence, onboarding/login, administrator RBAC, employees, devices, enrollment, permissions, policy compilation, audit, and agent endpoints.
- `CompanyDlp.AdminPortal`: Angular 21 standalone portal for onboarding, login, administrators, employees, devices, enrollment codes, permissions, base policy, and audit.
- `CompanyDlp.Contracts`: versioned IPC, policy, permission, audit, backend, encryption-key, and classification contracts.
- `CompanyDlp.Core`: testable business and file-protection logic.
- `CompanyDlp.Service`: Windows Service, protected policy cache, heartbeat/sync, audit outbox, USB/software/recorder monitoring, encryption, and session supervision.
- `CompanyDlp.Desktop`: per-user WPF agent for clipboard, watermark, screenshot/recording controls, notifications, and Explorer actions.
- `CompanyDlp.NativeHost` and `CompanyDlp.BrowserBridge`: authenticated browser integration.
- `CompanyDlp.MockServer`: isolated development backend that follows the agent contract.
- `CompanyDlp.Tests`: policy, permission, cryptographic, central policy, and synchronization tests.
- `browser-extension` and `firefox-extension`: managed browser protections.

## Requirements

- Windows 10/11 x64.
- .NET 8 SDK.
- SQL Server LocalDB for development, or another Microsoft SQL Server instance.
- Node.js 20+ and npm for the Angular portal.
- PowerShell 5.1 or PowerShell 7.
- Administrator PowerShell for machine policy, USB, service, and Explorer integration tests.

## Verify everything on Windows

```powershell
.\VERIFY_CENTRAL_ADMIN.bat
.\VERIFY_WINDOWS_READY.bat
```

`VERIFY_CENTRAL_ADMIN.bat` restores/builds the .NET solution, runs the .NET tests, performs a clean `npm ci`, and builds the Angular production bundle.

## Start the central administration system

```powershell
.\START_CENTRAL_ADMIN.bat
```

Development addresses:

- Admin API: `http://127.0.0.1:5060`
- Admin Portal: `http://127.0.0.1:4200`

Open the portal, create the first tenant/Owner through Onboarding, then create employees, enrollment codes, assign devices, and manage permissions.

The development API automatically applies the included initial migration to LocalDB. Production keeps automatic migration disabled.

## Connect a Windows endpoint

Create a one-time enrollment code in the portal, then run:

```powershell
.\CONNECT_DEVELOPMENT_TO_ADMIN.bat -TenantId '<tenant-guid>'
.\START_DEVELOPMENT.bat
```

When the central-development policy is active, `START_DEVELOPMENT.bat` health-checks the Admin API and does not start the local Mock Server.

## Permission model

Actions include screenshot, screen recording, clipboard, browser upload/drag-drop/paste, USB, software installation/execution, and file encryption/decryption.

A permission can be:

- `Allow` or `Block`;
- permanent or temporary using `ExpiresAtUtc`;
- normal or emergency deny;
- scoped to Global, Employee, Device, Department, User SID, Username, or Machine Name.

Emergency deny wins. Expired permissions stop applying locally even if the backend is temporarily unavailable.

## Administrator roles

- `Owner`: full management, including administrator accounts.
- `PolicyAdmin`: employees, devices, enrollment, permissions, policy, and audit.
- `Auditor`: read-only endpoint and administrator audit views.

Changing an administrator role/status/password increments its token version and immediately invalidates old JWTs. The last active Owner cannot be disabled or demoted.

## Security boundaries

- Production policy snapshots use ECDSA P-256/SHA-256.
- Device access tokens and enrollment codes are stored only as hashes.
- Admin passwords use PBKDF2-SHA256 with per-user salts and 210,000 iterations.
- File-key wrap/unwrap checks the relevant central permission and protects envelopes using ASP.NET Core Data Protection.
- Every central write creates an administrator audit entry.
- Endpoint events are identity-checked, size-limited, integrity-validated, and idempotent on `(TenantId, EventId)`.
- The endpoint keeps the last valid DPAPI-protected policy cache and remains fail-closed during backend outages.

## Production gates

This is a production-oriented user-mode architecture, not a claim of absolute kernel-level prevention. Before rollout complete the controls in `docs/PRODUCTION_GATES.md`, especially:

- Authenticode signing and a controlled release pipeline.
- WDAC/App Control and Windows device-control policy.
- Force-installed signed browser extensions and allowed-browser control.
- TLS, secret management, reviewed database migrations, backups, observability, and WAF/rate-limit policy.
- Protected ECDSA private key and KMS/HSM-backed file-key management.
- Windows acceptance testing from `docs/WINDOWS_TEST_PLAN.md`.

Read `QUICK_START_AR.md`, `docs/CENTRAL_ADMIN_API.md`, `docs/ARCHITECTURE.md`, `docs/BACKEND_INTEGRATION.md`, and `IMPLEMENTATION_REPORT_v1.1.0_AR.md` before deployment.
