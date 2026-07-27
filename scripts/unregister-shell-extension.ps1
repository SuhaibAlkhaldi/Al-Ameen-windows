#Requires -RunAsAdministrator
param(
    [Parameter(Mandatory = $true)]
    [string]$RegisterExe
)

$ErrorActionPreference = "SilentlyContinue"
$resolvedRegisterExe = (Resolve-Path $RegisterExe).Path
& $resolvedRegisterExe /unregister
Write-Host "CompanyDlp.ShellExtension (file Properties 'DLP' tab) unregistered." -ForegroundColor DarkGray
