# Company DLP architecture

## Components

1. **CompanyDlp.Service** — privileged policy engine, browser machine-policy enforcement, USB monitoring/blocking, audit logging, fragment tracking, and named-pipe API.
2. **CompanyDlp.Desktop** — per-user WPF agent for desktop watermark, clipboard monitoring, screenshot shortcut blocking, development test sessions, and local status.
3. **CompanyDlp.NativeHost** — Chrome/Edge Native Messaging bridge between the managed extension and the Windows service.
4. **browser-extension** — blocks file upload, file drag/drop, file/image paste, sensitive copy, sensitive input, and form submission; adds a repeated browser watermark.
5. **PowerShell scripts** — development start/restore and production publish/install/uninstall.

## Trust boundary

- Production service runs as LocalSystem.
- Employees should be Windows Standard Users.
- Production browser policies are written under HKLM and are reapplied by the service.
- Development browser policies are temporary HKCU values and are restored from a snapshot.
- No developer backdoor or hard-coded bypass password is included.

## Sensitive-data detection

Supported rule types:

- Keyword: blocks a word inside a larger copied sentence.
- ExactValue: blocks a specific email, account number, phrase, or other value.
- Regex: client-supplied pattern.
- AnyEmail: blocks any complete email address when enabled.

Exact values can use normalization, so punctuation, spaces, `@`, and dots do not avoid comparison. Fragment tracking combines recent copied fragments within a configured time window. Browser input protection additionally checks the final field value, so a user cannot paste `test`, type `@`, paste `gmail`, type `.`, and paste `com` inside a managed page.

## USB decision

- Any keyboard or mouse function is allowed.
- A bundle that has a forbidden function such as storage, WPD phone, network adapter, serial port, camera, printer, or media device is not treated as input-only.
- Existing USB bundles at first run form a trusted baseline to avoid disabling integrated laptop hardware that internally uses USB.
- Disconnect non-input external devices before creating the production baseline.
