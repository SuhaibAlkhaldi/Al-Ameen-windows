# Company DLP Windows Ready v1.0.6

## False-positive installation alert fix

- The first software-monitor scan now establishes a process baseline and does not emit alerts for processes that were already running before Company DLP started.
- Replaced broad `StartsWith("install")` / `Contains("installer")` matching with a conservative, testable classifier.
- Interactive user sessions are required for heuristic installer alerts; Session 0 background servicing processes are ignored.
- Microsoft Windows servicing processes such as `InstallAgentUserBroker.exe` and `TrustedInstaller.exe` are not reported as user installation attempts.
- Real installer signals remain detected, including `msiexec.exe`, Windows package installers, `.msi/.msix/.appx` command-line arguments, and installer/setup executable naming patterns outside the Windows system directory.
- Audit events now record the precise detection reason in `Method`.
- Added seven tests covering startup baselining and installer classification.
