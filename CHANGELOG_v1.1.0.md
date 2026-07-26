# Company DLP v1.1.0

## Central administration

- Added `CompanyDlp.AdminApi` using ASP.NET Core 8, EF Core, and Microsoft SQL Server.
- Added Angular 21 `CompanyDlp.AdminPortal` with onboarding, login, dashboard, administrators, employees, devices, enrollment, permissions, base policy, and audit pages.
- Added tenant onboarding and role-based administrator authentication.
- Added Owner, PolicyAdmin, and Auditor roles plus Owner-only administrator account management.
- Added immediate JWT revocation through per-account token versions when role, status, or password changes.
- Prevented disabling/demoting the current Owner session and prevented removal of the last active Owner.
- Added employee management, device enrollment/assignment/revocation, and one-time enrollment codes.
- Added permanent, temporary, and emergency-deny permissions with Global, Employee, Device, Department, SID, Username, and Machine scopes.
- Added base-policy validation/sanitization and monotonically increasing tenant policy revisions.
- Added per-device policy compilation so an endpoint never receives unrelated employees' grants.
- Added heartbeat-driven immediate policy refresh while preserving periodic polling as a fallback.
- Added ECDSA P-256/SHA-256 policy signing for Production and explicit unsigned Development mode only.
- Added opaque hashed device tokens, hashed one-time enrollment codes, and login/enrollment rate limiting.
- Added endpoint audit identity/schema/enum/time/size/integrity validation and SQL idempotency on `(TenantId, EventId)`.
- Added server-side permission checks for file-key wrapping/unwrapping and ASP.NET Core Data Protection envelopes.
- Added administrator audit records for central writes.
- Added SQL foreign keys, uniqueness rules, and the initial EF Core migration.
- Added scripts for central startup, verification, onboarding, enrollment, device listing, policy signing keys, and permission changes.
- Updated the development launcher to use the Admin API when a central-development policy is configured, otherwise retaining the local Mock Server.
