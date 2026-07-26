[CmdletBinding()]
param(
    [string]$OutputDirectory = ".\secrets\policy-signing"
)

$ErrorActionPreference = "Stop"
$fullOutput = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $fullOutput -Force | Out-Null
$ecdsa = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve]::NamedCurves.nistP256)
try {
    if (-not $ecdsa.PSObject.Methods.Name.Contains("ExportPkcs8PrivateKeyPem")) {
        throw "This script requires PowerShell 7 on .NET 8 or newer."
    }
    $privatePath = Join-Path $fullOutput "company-dlp-policy-private.pem"
    $publicPath = Join-Path $fullOutput "company-dlp-policy-public.pem"
    [IO.File]::WriteAllText($privatePath, $ecdsa.ExportPkcs8PrivateKeyPem())
    [IO.File]::WriteAllText($publicPath, $ecdsa.ExportSubjectPublicKeyInfoPem())
    Write-Host "Private key: $privatePath" -ForegroundColor Yellow
    Write-Host "Public key:  $publicPath" -ForegroundColor Green
    Write-Host "Keep the private key only on the backend secret store. Put only the public key in the Windows agent production policy." -ForegroundColor Cyan
}
finally {
    $ecdsa.Dispose()
}
