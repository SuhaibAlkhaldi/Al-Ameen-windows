# Known Limitations and Honest Security Boundaries

## Screenshot and recording

A user-mode Windows application cannot mathematically guarantee prevention of every screen-capture technique across every third-party application. This build blocks configured keyboard shortcuts, built-in capture tools/known recorder processes, browser `getDisplayMedia`, and protects the DLP window with display affinity where supported. It logs the process/method it can identify.

It cannot prevent an external camera, malicious kernel driver, compromised administrator, or every undocumented/API-injection capture path. High-assurance environments require WDAC allowlisting, restricted admin rights, controlled remote-support tools, and potentially a separately designed/signed driver after threat-model review.

## Software installation

The user-mode `Block` mode detects and terminates common installer processes after process creation. Production policy is deliberately configured for `WindowsAppControl`, where Windows blocks unapproved code before normal execution and the agent ingests Code Integrity events. A signed, tested WDAC policy is an external deployment artifact and is not auto-generated from this repository.

## USB

Development `Block` uses PnPUtil after device arrival. This provides useful testing but may allow a short detection window. Production should deploy Device Installation Restrictions or Microsoft Defender Device Control so unapproved storage/mobile/composite devices are denied before usable access. HID spoofing is mitigated only by VID/PID/serial/hardware-ID allowlists, not by device class alone.

## Browser scope

The extension protects managed Chrome/Edge/Firefox profiles. An unmanaged browser or executable can bypass extension-only controls. Production must force-install the extension, block extension removal, and restrict execution to managed browsers with WDAC/App Control.

Browser APIs do not normally reveal full local file paths. Audit events therefore record filename, extension, size, MIME type, hash when available, browser, and sanitized destination—not a fabricated path.


## AI-classified browser allow flow

The classification provider and HTTP contract are implemented, but the current browser policy remains fail-closed `BlockAll`. A secure asynchronous allow path needs a short-lived approval token bound to the exact user/device/destination/file metadata or hash. Automatically replaying a previously blocked browser event would be unsafe and is not implemented.

## Development browser fallback

The loopback HTTP fallback exists only for managed temporary Chrome/Edge development profiles when an unsigned Debug Native Host is blocked by Smart App Control. It is not a Production transport and Firefox development still requires a Native Host that Windows allows to execute.

## Clipboard images/OCR

Text classification is implemented. File/image paste is currently blocked as a file action. Detecting sensitive text inside arbitrary images requires a trusted OCR/AI provider and is intentionally not simulated.

## Administrator and physical attacks

A local administrator or offline attacker can ultimately alter a standard Windows endpoint. Production requires BitLocker, Secure Boot, restricted local admin, signed binaries, protected service ACLs, WDAC, and central health monitoring.
