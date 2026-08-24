#Requires -RunAsAdministrator
<#
================================================================================
 reset-agent-identity.ps1
================================================================================
 Purpose : Clear this device's local Company DLP enrollment state so it can be
           cleanly re-enrolled with a NEW enrollment code - use this ONLY after
           the backend's Devices table for this device (or the whole tenant)
           has been removed/reset server-side (e.g. wipe-production-database.sql
           was run), and --enroll is failing or silently no-opping because the
           local identity/policy cache still point at data that no longer
           exists on the server.

 NOT run automatically by install/uninstall - uninstall-production.ps1
 deliberately leaves everything under ProgramData\CompanyDlp in place for
 audit/investigation purposes (see that script's final Write-Host). This is a
 separate, explicit, opt-in tool for the one specific "server-side data was
 reset, local agent needs to forget its old identity" scenario - confirmed
 live (2026-08-24): without this, re-enrolling after a backend reset silently
 fails via two different stale-cache files with no clear error, and took a
 long manual investigation to diagnose:
   1. identity.json keeps the OLD deviceId, which the backend enrollment
      check silently rejects with "device already enrolled" once it exists
      again, or just runs the service against a deviceId the server has
      never heard of if enrollment is skipped.
   2. Policy\remote-policy.cache keeps the OLD signed policy snapshot
      (including its own tenantId in some builds), which gets loaded back in
      over the fresh local policy.json on every startup.
   3. Credentials\*.bin keeps the OLD device access token, which is
      meaningless once the server-side Devices/DeviceCredentials rows are
      gone.

 This script stops the service, deletes those three, and leaves policy.json
 and audit/log files untouched. After running it, re-enroll with:
   $env:COMPANY_DLP_ENROLLMENT_CODE = "<new code>"
   & "$env:ProgramFiles\CompanyDlp\Service\CompanyDlp.Service.exe" --enroll
 (or use enroll-production-agent.ps1)
================================================================================
#>

param(
    [switch]$Force
)

$ErrorActionPreference = "Stop"

if (-not $Force) {
    Write-Host "This will erase this device's LOCAL enrollment identity, cached policy, and stored credential." -ForegroundColor Yellow
    Write-Host "Only do this if the backend no longer has a record of this device (e.g. after a production DB reset)." -ForegroundColor Yellow
    Write-Host "Audit logs and policy.json are NOT touched." -ForegroundColor Yellow
    $confirmation = Read-Host "Type YES to continue"
    if ($confirmation -ne "YES") {
        Write-Host "Aborted - nothing was changed." -ForegroundColor Red
        exit 1
    }
}

$programDataRoot = Join-Path $env:ProgramData "CompanyDlp"

Write-Host "Stopping the CompanyDlp service..." -ForegroundColor Cyan
Stop-Service CompanyDlp -Force -ErrorAction SilentlyContinue

$targets = @(
    Join-Path $programDataRoot "Agent\identity.json",
    Join-Path $programDataRoot "Policy\remote-policy.cache"
)

foreach ($path in $targets) {
    if (Test-Path $path) {
        Remove-Item $path -Force
        Write-Host "Removed: $path" -ForegroundColor Green
    } else {
        Write-Host "Not present (already clean): $path" -ForegroundColor DarkGray
    }
}

$credentialsDir = Join-Path $programDataRoot "Credentials"
if (Test-Path $credentialsDir) {
    Remove-Item (Join-Path $credentialsDir "*.bin") -Force -ErrorAction SilentlyContinue
    Write-Host "Removed stored credential(s) under: $credentialsDir" -ForegroundColor Green
} else {
    Write-Host "Not present (already clean): $credentialsDir" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "Local enrollment identity reset. Re-enroll now with a valid enrollment code:" -ForegroundColor Green
Write-Host '  $env:COMPANY_DLP_ENROLLMENT_CODE = "<new code>"' -ForegroundColor White
Write-Host '  & "$env:ProgramFiles\CompanyDlp\Service\CompanyDlp.Service.exe" --enroll' -ForegroundColor White
Write-Host "Then: Start-Service CompanyDlp" -ForegroundColor White
