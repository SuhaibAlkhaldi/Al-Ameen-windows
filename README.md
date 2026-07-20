# Company DLP Windows Ready v1

This repository is the Windows endpoint implementation prepared for functional testing before the central ASP.NET Core/SQL Server backend is built.

The Windows agent already uses the final versioned contracts for policy synchronization, heartbeat, audit-event batching, device enrollment, file-key wrapping, and future AI file classification. During testing, `CompanyDlp.MockServer` implements those same contracts locally. A real backend can replace the mock by implementing `contracts/company-dlp-agent-api.openapi.yaml` and changing production configuration; the Windows enforcement code does not need to be rewritten.

## Projects

- `CompanyDlp.Contracts`: versioned IPC, policy, permission, audit, backend, encryption-key, and file-classification contracts.
- `CompanyDlp.Service`: Windows Service, policy engine, encrypted audit outbox, backend synchronization, USB/software/recorder monitoring, file encryption, and session-agent supervision.
- `CompanyDlp.Desktop`: per-user WPF session agent for clipboard, watermark, screenshot shortcuts, recorder controls, notifications, and Explorer commands.
- `CompanyDlp.NativeHost`: authenticated browser Native Messaging bridge.
- `CompanyDlp.MockServer`: development-only backend simulator using the final API contracts.
- `CompanyDlp.Tests`: policy and cryptographic tests.
- `browser-extension`: Chrome/Edge Manifest V3 protection.
- `firefox-extension`: Firefox development protection.

## First run

Requirements:

- Windows 10/11 x64.
- .NET 8 SDK.
- PowerShell 5.1 or PowerShell 7.
- Administrator PowerShell for USB/device and machine-policy tests.
- Node.js is optional and used only for JavaScript syntax validation.

Run verification first:

```powershell
.\VERIFY_WINDOWS_READY.bat
```

Then run the complete development environment:

```powershell
.\START_DEVELOPMENT.bat
```

The development runner builds the solution, starts the contract-compatible Mock Server, starts the Windows service process, registers temporary Explorer actions, and opens the Desktop through signed `dotnet.exe` so Smart App Control does not block an unsigned Debug apphost.

Close the Company DLP window to stop the development environment and restore temporary registry/browser changes.

## Temporary permission testing

Keep `START_DEVELOPMENT.bat` running, then open another PowerShell window:

```powershell
.\SET_DEVELOPMENT_PERMISSION.bat
```

The grant is scoped to the current Windows user SID and has a UTC expiry. The endpoint reevaluates the policy locally, so the permission expires without a command from the backend. A `TemporaryPermissionExpired` audit event is queued automatically.

View synchronized events:

```powershell
.\SHOW_DEVELOPMENT_EVENTS.bat
```

## Backend replacement

The real backend must implement:

```text
POST /api/v1/agent/enroll
POST /api/v1/agent/heartbeat
GET  /api/v1/agent/policy
POST /api/v1/agent/events/batch
POST /api/v1/agent/file-classification
POST /api/v1/agent/file-keys/wrap
POST /api/v1/agent/file-keys/unwrap
```

The authoritative contract is `contracts/company-dlp-agent-api.openapi.yaml`.

Production communication requires HTTPS, a DPAPI-protected device bearer token, signed policy snapshots, and a trusted code-signing pipeline. Development permits only the local mock and an explicitly marked unsigned development policy.

After production installation and policy configuration, enroll without putting the one-time code in the process command line:

```powershell
.\scripts\enroll-production-agent.ps1 -EnrollmentCode '<one-time-code>'
```

## Important production gates

This is a test-ready Windows implementation, not a claim that a user-mode prototype alone can provide absolute kernel-level DLP. Before rollout, complete the gates in `docs/PRODUCTION_GATES.md`, especially:

- Authenticode signing for every EXE/DLL/installer.
- A tested WDAC/App Control policy for installation and unapproved execution prevention.
- Device Installation Restrictions or Microsoft Defender Device Control for pre-access USB enforcement.
- Force-installed signed browser extensions and allowed-browser control.
- Production backend, enrollment, ECDSA policy signing, and KMS/HSM integration.
- Windows VM/device acceptance testing from `docs/WINDOWS_TEST_PLAN.md`.

See `docs/WINDOWS_READY_ARCHITECTURE.md`, `docs/BACKEND_INTEGRATION.md`, and `docs/KNOWN_LIMITATIONS.md` before production use.


## v1.0.3 testability boundary

Business rules and file-protection logic are isolated in `CompanyDlp.Core`; unit tests never reference executable hosts.


## v1.0.4 startup fix
Development startup launches Mock Server, Service, and Desktop through the signed `dotnet.exe` host to remain compatible with Smart App Control. Runtime logs are written under `.development/logs`.

## v1.0.5 launcher diagnostics

Development startup creates logs before the first runtime step:

- `.development/logs/launcher.log`
- `.development/logs/launcher.error.log`
- `.development/logs/launcher.transcript.log`
- `.development/logs/mock-server.stdout.log`
- `.development/logs/mock-server.stderr.log`
- `.development/logs/service.stdout.log`
- `.development/logs/service.stderr.log`

Use `START_DEVELOPMENT_CORE_ONLY.bat` to isolate Mock Server, Service, and Desktop startup from File Explorer shell registration.

## v1.0.6 startup false-positive fix

The software monitor establishes a baseline on startup and no longer reports existing Windows servicing processes as user installation attempts. See `CHANGELOG_v1.0.6.md`.
