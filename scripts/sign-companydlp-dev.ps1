param(
    [string]$ProjectRoot = "C:\Users\Suhaib\Desktop\CompanyDlp_v1.1.0_CentralAdmin_Source\CompanyDlp_v1.1.0_CentralAdmin_Source"
)

$ErrorActionPreference = "Stop"

$certSubject = "CN=CompanyDlp Development Code Signing"

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
$isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    throw "Run this script from PowerShell as Administrator because it writes to LocalMachine certificate stores."
}

Write-Host "Looking for development code-signing certificate..."

$cert = Get-ChildItem Cert:\LocalMachine\My |
    Where-Object {
        $_.Subject -eq $certSubject -and
        $_.HasPrivateKey -and
        $_.NotAfter -gt (Get-Date)
    } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if (-not $cert) {
    Write-Host "Creating development code-signing certificate..."

    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $certSubject `
        -CertStoreLocation "Cert:\LocalMachine\My" `
        -KeyExportPolicy Exportable `
        -KeyUsage DigitalSignature `
        -KeyLength 2048 `
        -HashAlgorithm SHA256
}

$certPath = Join-Path $env:TEMP "CompanyDlpDevCodeSigning.cer"

Write-Host "Exporting development certificate..."
Export-Certificate `
    -Cert $cert `
    -FilePath $certPath `
    -Force | Out-Null

Write-Host "Trusting development certificate locally..."

Import-Certificate `
    -FilePath $certPath `
    -CertStoreLocation "Cert:\LocalMachine\Root" | Out-Null

Import-Certificate `
    -FilePath $certPath `
    -CertStoreLocation "Cert:\LocalMachine\TrustedPublisher" | Out-Null

$folders = @(
    "src\CompanyDlp.Desktop\bin\Debug\net8.0-windows",
    "src\CompanyDlp.Service\bin\Debug\net8.0-windows",
    "src\CompanyDlp.NativeHost\bin\Debug\net8.0-windows",
    "src\CompanyDlp.BrowserBridge\bin\Debug\net8.0-windows",
    "src\CompanyDlp.Core\bin\Debug\net8.0-windows",
    "src\CompanyDlp.Contracts\bin\Debug\net8.0"
)

$files = foreach ($folder in $folders) {
    $full = Join-Path $ProjectRoot $folder

    if (Test-Path $full) {
        Get-ChildItem $full -Recurse -Include *.exe, *.dll -File
    }
}

$files = $files | Sort-Object FullName -Unique

if (-not $files) {
    throw "No binaries found to sign. Build the solution first."
}

Write-Host "Signing $($files.Count) files using Set-AuthenticodeSignature..."

foreach ($file in $files) {
    Write-Host "Signing $($file.FullName)"

    $signature = Set-AuthenticodeSignature `
        -FilePath $file.FullName `
        -Certificate $cert `
        -HashAlgorithm SHA256

    if ($signature.Status -ne "Valid") {
        throw "Failed to sign $($file.FullName). Status=$($signature.Status). Message=$($signature.StatusMessage)"
    }
}

Write-Host ""
Write-Host "Development signing completed successfully."
Write-Host "Certificate subject: $($cert.Subject)"
Write-Host "Certificate thumbprint: $($cert.Thumbprint)"
Write-Host "Certificate export path: $certPath"