#Requires -RunAsAdministrator
param(
    [Parameter(Mandatory = $true)]
    [string]$RegisterExe
)

$ErrorActionPreference = "Stop"
$resolvedRegisterExe = (Resolve-Path $RegisterExe).Path

# The actual CLSID/InprocServer32/PropertySheetHandlers registry shape for a .NET-hosted COM shell
# extension is fragile to hand-write (RuntimeVersion/CodeBase/etc.) - CompanyDlp.ShellExtension.
# Register.exe does this via SharpShell's own ServerRegistrationManager instead, the one deliberate
# deviation from this repo's usual "always hand-write raw registry keys" convention (see
# register-production-context-menu.ps1 for that convention elsewhere).
#
# This registers a property SHEET page (a new "DLP" tab in the file Properties dialog), not a
# hover tooltip - an earlier IQueryInfo/InfoTip-based approach was tried and proven, via an
# isolated test, to replace Explorer's entire default tooltip instead of appending to it, so it
# was abandoned in favor of this strictly-additive extension point.
& $resolvedRegisterExe /register
if ($LASTEXITCODE -ne 0) {
    throw "CompanyDlp.ShellExtension registration failed (exit code $LASTEXITCODE)."
}

Write-Host "CompanyDlp.ShellExtension (file Properties 'DLP' tab) registered for all users." -ForegroundColor Green
Write-Host "CompanyDlp.ShellExtension (Explorer 'Classification' column, via the Windows Property System) also registered." -ForegroundColor Green
Write-Host "Explorer may need to be restarted (or the user signed out/in) before the tab appears." -ForegroundColor DarkYellow
Write-Host "The Classification column will not be visible by default - enable it once via right-click on a column header > More... (Windows never auto-shows a third-party column, regardless of how it was registered)." -ForegroundColor DarkYellow
