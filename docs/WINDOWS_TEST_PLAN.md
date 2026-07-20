# Windows Functional Test Plan

## 1. Verify and start

Open PowerShell as Administrator in the repository:

```powershell
.\VERIFY_WINDOWS_READY.bat
.\START_DEVELOPMENT.bat
```

In the Desktop window, confirm Connected status, one policy path under the root `config` directory, Mock backend mode, and zero/expected pending audit count. Start the test session.

## 2. Clipboard

- Copy ordinary text: allowed.
- Copy configured keyword/email/IBAN candidate: blocked and clipboard cleared.
- Copy sensitive value in fragments: final assembling operation blocked.
- Add temporary `clipboard.copy-sensitive` permission: sensitive copy allowed until expiry, then blocked automatically.
- Verify events contain rule ID/masked information but no full sensitive value.

## 3. Screenshot and recording

Test Print Screen, Win+Shift+S, Game Bar shortcuts, Snipping Tool, and each configured recorder available on the test image. Verify block/detection, process/method attribution, and audit events. Document any unsupported capture application rather than marking it blocked.

## 4. Watermark

Verify username, machine name, date/time on each monitor. Change resolution, connect/disconnect a monitor, lock/unlock, and resume from sleep. Confirm only one click-through overlay per monitor.

## 5. Browser

Use the protected temporary Chrome/Edge profile opened by **Start Test Session**. Test file picker, input change, drag/drop, file paste, image paste, Web Share, and downloads on multiple sites. Verify hostname/sanitized destination/action/file metadata. Confirm ordinary page text input works unless a sensitive rule matches. Then temporarily make the Debug Native Host unavailable and verify Chrome/Edge uses the managed development loopback fallback and still queues events through the Service; restore and verify cleanup. Do not mark Firefox fallback as passed because it is intentionally not implemented.

## 6. USB

Use approved mouse/keyboard, USB storage, phone, composite device, and an allowlisted VID/PID/serial device. Verify allow/block/audit reason and hardware metadata. Development PnP blocking is not the final pre-access production control.

## 7. Software

In AuditOnly, run MSI, MSIX/AppX/winget, setup-named EXE, and portable EXE; verify accurate detection without false “blocked” claims. On a WDAC pilot VM, configure `WindowsAppControl` and confirm Code Integrity 3077/3033 events become `UnapprovedExecutionBlocked` audits.

## 8. Encrypt/decrypt

Use Explorer right-click and Desktop buttons on empty, small, multi-megabyte, Unicode-name, and read-only-access scenarios. Verify:

- `.dlpenc` v2 created;
- original deleted only after verification;
- tampered ciphertext fails and creates no plaintext;
- unauthorized user/path access is denied by the caller token;
- duplicate output names are handled safely;
- transaction events share correlation ID.

## 9. Temporary permissions

Grant each ActionKey for 1–2 minutes. Confirm it applies without restarting, expires using UTC locally, reverts enforcement, and queues `TemporaryPermissionExpired` once.

## 10. Offline outbox

While the system is running, stop only the Mock Server process. Trigger several actions and confirm endpoint protections continue. Restart the Mock Server and confirm pending events synchronize, duplicate IDs are not inserted, and the queue drains only after acknowledgement.

## 11. Restart/tamper

Restart the machine/service, close the Session Agent in Production pilot mode, modify policy/cache/outbox files as a standard user, stop service as a standard user, remove extension, and change system time. Replay the same IPC `MessageId` and send a stale timestamp; both must be rejected. In a Production-policy simulation, move the local clock backward and confirm temporary permission evaluation fails closed with `ClockRollbackDetected` or `TrustedTimeUnavailable`. Record expected denial/recovery and every gap.

## 12. Exit criteria

A feature passes only when enforcement result, user/device/process/site metadata, policy/grant reason, offline queue, backend synchronization, UI visibility, expiry behavior, and negative/tamper tests all pass.
