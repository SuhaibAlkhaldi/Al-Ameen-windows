# Central Company DLP Administration

## Architecture

`CompanyDlp.AdminApi` and `CompanyDlp.AdminPortal` form the central control plane for the Windows endpoint.

```text
Angular Admin Portal
        |
        | administrator JWT + role policy
        v
ASP.NET Core Admin API
        |
        | EF Core transactions and audit records
        v
Microsoft SQL Server
        |
        | heartbeat revision + device-targeted snapshot
        v
Windows Agent
        |
        | identity/version/expiry/signature validation
        v
Local PermissionEvaluator and enforcement components
```

The administrator does not remotely execute a command on an employee computer. A central write changes the database and increments `Tenant.PolicyRevision`. Heartbeat returns `PolicyRefreshRequired=true` when the device is behind. The agent wakes its policy worker, downloads only its own compiled snapshot, verifies it, and atomically replaces the protected cache.

If the backend is unavailable, the last valid DPAPI-protected policy remains active. Temporary permissions also expire locally using trusted server-time anchoring.

## Portal

The Angular portal is under `src/CompanyDlp.AdminPortal` and exposes:

- tenant onboarding and login;
- administrator account management;
- employees and departments;
- enrolled devices, assignment, revocation, and one-time enrollment codes;
- permanent/temporary/emergency permissions;
- validated base-policy JSON;
- endpoint security events and administrator audit history.

Development starts through:

```powershell
.\START_CENTRAL_ADMIN.bat
```

The portal uses a development proxy to `http://127.0.0.1:5060`, so browser calls use relative `/api` routes.

## Administrator roles

- `Owner`: all central-management capabilities and administrator account management.
- `PolicyAdmin`: employees, devices, enrollment, permissions, policy, and audit.
- `Auditor`: endpoint and administrator audit read access only.

The API revalidates the administrator account and tenant on each JWT-authenticated request. Role/status/password changes increment `TokenVersion`, immediately invalidating previously issued tokens. The API prevents deactivating or demoting the current Owner session and prevents removal of the last active Owner.

## Permission precedence and scopes

Supported scopes:

- `Global`
- `Employee`
- `Device`
- `Department`
- `UserSid`
- `Username`
- `MachineName`

A grant can be permanent or have `ExpiresAtUtc`. An emergency deny is represented by `EmergencyDeny=true` and `Allowed=false`; it takes precedence over ordinary grants. The existing endpoint evaluator remains the source of truth for local action decisions.

Action keys are returned by `GET /api/v1/admin/actions`. They cover screenshot, screen recording, clipboard, browser transfers, USB, software installation/execution, file encryption/decryption, and agent session control.

## Development workflow

Requirements:

- .NET 8 SDK;
- Node.js 20+ and npm;
- SQL Server LocalDB or another SQL Server instance.

Verify first:

```powershell
.\VERIFY_CENTRAL_ADMIN.bat
```

Start API and portal:

```powershell
.\START_CENTRAL_ADMIN.bat
```

Create the first tenant through the portal Onboarding page, then create a one-time enrollment code. Connect the Windows endpoint:

```powershell
.\CONNECT_DEVELOPMENT_TO_ADMIN.bat -TenantId '<tenant-guid>'
.\START_DEVELOPMENT.bat
```

Equivalent PowerShell administration helpers remain available:

```powershell
.\scripts\admin-onboard-development.ps1 -Email admin@company.test
.\scripts\admin-create-enrollment-code.ps1 -Email admin@company.test
.\scripts\admin-list-devices.ps1 -Email admin@company.test
.\scripts\admin-set-permission.ps1 `
  -Email admin@company.test `
  -ActionKey screen.capture `
  -Allowed $true `
  -ScopeType Device `
  -ScopeId '<device-guid>' `
  -ExpiresInMinutes 10 `
  -Reason 'Approved support session'
```

## Database model

The initial migration creates:

- `Tenants`
- `TenantPolicies`
- `AdminUsers`
- `Employees`
- `Devices`
- `EnrollmentCodes`
- `PermissionGrants`
- `SecurityEvents`
- `AdminAuditLogs`

Development may use `Database:AutoMigrate=true`. Production should disable automatic migration and apply a reviewed migration in the deployment pipeline.

## Policy compilation and delivery

The compiler loads the tenant base policy, sanitizes it, and selects only grants applicable to the authenticated device and its assigned employee. Employee/department grants are projected to the best concrete identity available. Unrelated employee grants are not sent to the endpoint.

Production snapshots:

- target one tenant and one device;
- use a monotonically increasing revision;
- contain issue and expiry timestamps;
- are signed with ECDSA P-256/SHA-256 over the canonical payload;
- are rejected by the endpoint when tenant/device/version/time/signature checks fail.

Development can explicitly allow unsigned snapshots only when `PolicyDelivery:Mode=Development` and the development flag is enabled.

## Security controls

- Admin passwords: PBKDF2-SHA256, per-user salt, 210,000 iterations.
- Admin tokens: short-lived JWTs with issuer/audience/lifetime/signature validation and database-backed token-version revocation.
- Device credentials: cryptographically random opaque tokens; only SHA-256 hashes are stored.
- Enrollment: one-time, hashed, expiring codes with rate limiting.
- Policy input: canonical action defaults, collection limits, regex validation/timeout, and payload-size limits.
- Audit ingestion: authenticated identity match, schema validation, enum validation, time bounds, field/detail limits, integrity hash, and idempotency.
- File keys: central `file.encrypt`/`file.decrypt` permission recheck plus ASP.NET Core Data Protection envelope storage.
- Central writes: administrator identity and change details recorded in `AdminAuditLogs`.
- Tenant isolation: tenant filters and foreign keys on all central entities.

## Production configuration

1. Configure a production SQL Server connection string through secret/configuration management.
2. Set a strong `Jwt__SigningKey`, and configure issuer/audience.
3. Generate an ECDSA P-256 key pair:

```powershell
pwsh .\scripts\generate-policy-signing-keys.ps1
```

4. Store the private key only in the backend secret store using `PolicyDelivery__SigningPrivateKeyPath` or `PolicyDelivery__SigningPrivateKeyPem`.
5. Place only the public key in the endpoint bootstrap policy.
6. Set `PolicyDelivery__Mode=Production`, disable unsigned snapshots, and set the public HTTPS URL.
7. Protect the Data Protection key ring using certificate/Key Vault/HSM controls or replace the abstraction with the organization KMS.
8. Serve the Angular production build through an approved web tier and route `/api` to the Admin API.
9. Run behind TLS, reverse proxy/WAF, centralized logging, monitored rate limits, backups, and reviewed migrations.
10. Disable public onboarding after the initial controlled bootstrap.

The administrator OpenAPI contract is `contracts/company-dlp-admin-api.openapi.yaml`. The endpoint contract is `contracts/company-dlp-agent-api.openapi.yaml`.
