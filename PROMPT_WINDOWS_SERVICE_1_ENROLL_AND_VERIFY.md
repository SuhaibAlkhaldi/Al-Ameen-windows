# Task: Configure, enroll, and run the Windows agent against the real backend (no code changes)

## Important framing
This is **not a coding task**. `win-form/Al-Ameen-windows/src/CompanyDlp.Service` (and its OpenAPI contract) was
already verified to match the backend's agent-facing API exactly — routes, DTO shapes, and the
`DeviceBearerToken` auth scheme all line up. Do not edit any `.cs` files in this repo as part of this task. If you
hit an actual contract mismatch (an error that implies a field/route the backend doesn't support), stop and
report the exact error instead of patching around it — that would mean the backend regressed, which is a
different problem to fix on that side.

What you're actually doing here: local configuration, running the enrollment flow, starting the service, and
verifying it's really talking to the backend end-to-end. You have real shell/filesystem access on this machine,
which is exactly what's needed for this — the previous analysis of this project was done without that access.

## Assumptions / prerequisites
- The backend (`Al-AmeenBackend/DLPManagementSystem`) is running locally at `https://localhost:7008` (start it
  with `dotnet run` from that project if it isn't already running — check `curl -k https://localhost:7008/health/live`
  first).
- A seeded dev enrollment code already exists: plaintext `DEV-ENROLLMENT-TOKEN`, for the organization with
  `Code = "DEV"` ("Development Organization"). You don't need to create a new one for this pass — treat this as
  the source of truth if it works, and only ask the user for a different code if it's been revoked/expired.

## Steps

1. **Confirm the backend is reachable and get the real `tenantId`.**
   Call `POST https://localhost:7008/api/v1/auth/login` with
   `{"email":"dev.admin@companydlp.local","password":"DevAdmin123!"}` (`curl -k` is fine for local dev TLS).
   Read `data.user.organizationId` from the response — that GUID is the `tenantId` you need everywhere below.
   If login fails, stop and report the exact response — don't guess a tenantId.

2. **Trust the backend's local dev HTTPS certificate**, if not already trusted, so the agent's `HttpClient` will
   accept it: `dotnet dev-certs https --trust`. (Skip if a quick `curl` without `-k` to `/health/live` already
   succeeds.)

3. **Find or create the local policy file.** Its path is resolved by `ResolvePolicyPath()` in
   `src/CompanyDlp.Core/PolicyStore.cs` — normally
   `%ProgramData%\CompanyDlp\policy.json` (confirm by reading that method; it may differ from this default on
   this machine/OS). If the file doesn't exist yet, running the service once (`dotnet run --project
   src/CompanyDlp.Service -- ` with no args, then stop it) will create a default one you can then edit — or build
   the JSON yourself using `config/policy.production.sample.json` as the structural template.

4. **Edit the policy file's `backend` and `fileClassification` sections** (leave everything else as-is):
   ```json
   "backend": {
     "enabled": true,
     "tenantId": "<organizationId from step 1>",
     "mode": "Development",
     "baseUrl": "https://localhost:7008",
     "requestTimeoutSeconds": 15,
     "auditBatchSize": 100,
     "auditSyncSeconds": 5,
     "policySyncSeconds": 15,
     "allowUnsignedDevelopmentPolicy": true,
     "policySigningPublicKeyPem": "",
     "authenticationMode": "DeviceBearerToken",
     "credentialName": "agent-access-token"
   }
   ```
   Also make sure the top-level `"runtime": { "mode": "Development", ... }` stays `Development` — do **not** set
   it to `Production` in this pass (the agent's `ProductionReadinessValidator` will hard-reject a production run
   against a localhost backend with no real policy signing key, on purpose).

5. **Enroll the device.** From `src/CompanyDlp.Service`, run (PowerShell):
   ```powershell
   $env:COMPANY_DLP_ENROLLMENT_CODE = "DEV-ENROLLMENT-TOKEN"
   dotnet run -- --enroll
   ```
   This should print something like `Company DLP device <guid> enrolled. Credential expires at <date>.` — that
   confirms `POST /api/v1/agent/enroll` worked and the access token was saved locally (DPAPI-protected, via
   `AgentCredentialStore`). If it fails, report the exact console output.

6. **Run the service normally** (still `dotnet run` from `src/CompanyDlp.Service`, no `--enroll` this time — or
   `dotnet run` in a separate terminal so you can watch logs live). Watch the logs for:
   - A successful heartbeat cycle (`HeartbeatWorker`) — no repeated 401s.
   - A successful policy fetch (`PolicySyncWorker`) — 200 or 204, not an error.
   Let it run for at least one full heartbeat + policy sync interval (default ~15s each) before judging it.

7. **Verify from the backend side.** Call `GET https://localhost:7008/api/v1/devices` with the JWT from step 1
   (`Authorization: Bearer <accessToken>`) and confirm the newly enrolled device shows up with a recent
   `lastSeenAtUtc`.

8. **Don't install it as a Windows Service yet** — running it interactively via `dotnet run` is enough to prove
   the integration works. Installing as an actual service (`scripts/install-production.ps1` or `sc create`) is a
   separate, later step once this pass is confirmed working.

## Report back
For each step, say whether it succeeded, and paste the exact error text for anything that failed rather than
summarizing it. If everything in steps 1–7 succeeds, that's the whole integration confirmed end-to-end.
