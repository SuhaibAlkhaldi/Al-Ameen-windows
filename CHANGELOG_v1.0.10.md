# Company DLP Windows Ready v1.0.10

- Fixed Development startup on machines where Application Control blocks direct loading of `CompanyDlp.Desktop.dll` through `dotnet.exe`.
- Development launcher now starts `CompanyDlp.Desktop.exe` through the Windows apphost executable.
- Development File Explorer Encrypt/Decrypt actions now call `CompanyDlp.Desktop.exe` directly.
- Software installation monitoring is now dormant in Development mode until the user selects **Start test session**.
- Stopping the test session removes the existing session marker, causing software monitoring to stop automatically.
- A fresh process baseline is established after every test-session start, preventing existing processes from generating false installer alerts.
- Production software protection remains continuously active and is not affected by the Development test-session gate.
- Added regression tests for Development session gating and process baseline reset.
- Agent assembly version updated to `3.0.4`.
