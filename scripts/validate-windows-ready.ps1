#Requires -Version 5.1
$ErrorActionPreference = "Stop"

# Remove Mark-of-the-Web from this developer-owned extracted source tree before compilation.
# Production signature validation remains enforced separately.
Get-ChildItem (Join-Path $PSScriptRoot '..') -Recurse -File -ErrorAction SilentlyContinue |
    Unblock-File -ErrorAction SilentlyContinue
$root = Split-Path -Parent $PSScriptRoot
$failures = [System.Collections.Generic.List[string]]::new()

function Assert-Command([string]$Name) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        $failures.Add("Required command is missing: $Name")
    }
}

function Assert-LastExitCode([string]$Step) {
    if ($LASTEXITCODE -ne 0) { throw "$Step failed with exit code $LASTEXITCODE." }
}

Push-Location $root
try {
    Assert-Command "dotnet"
    if ($failures.Count -gt 0) { throw ($failures -join [Environment]::NewLine) }

    $sdkMajor = [int]((dotnet --version).Split('.')[0])
    if ($sdkMajor -lt 8) { throw ".NET SDK 8 or newer is required." }

    Write-Host "[1/7] Restoring packages..." -ForegroundColor Cyan
    dotnet restore .\CompanyDlp.sln
    Assert-LastExitCode "dotnet restore"

    Write-Host "[2/7] Building Release..." -ForegroundColor Cyan
    dotnet build .\CompanyDlp.sln -c Release --no-restore
    Assert-LastExitCode "dotnet build"

    Write-Host "[3/7] Running tests..." -ForegroundColor Cyan
    dotnet test .\CompanyDlp.sln -c Release --no-build
    Assert-LastExitCode "dotnet test"

    Write-Host "[4/7] Validating JSON and manifests..." -ForegroundColor Cyan
    @(
        ".\config\policy.development.json",
        ".\config\policy.production.sample.json",
        ".\browser-extension\manifest.json",
        ".\browser-extension\managed-storage-schema.json",
        ".\firefox-extension\manifest.json"
    ) | ForEach-Object { Get-Content $_ -Raw | ConvertFrom-Json | Out-Null }

    Write-Host "[5/7] Validating PowerShell syntax..." -ForegroundColor Cyan
    Get-ChildItem .\scripts -Filter *.ps1 -File | ForEach-Object {
        $tokens = $null
        $errors = $null
        [System.Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$tokens, [ref]$errors) | Out-Null
        if ($errors.Count -gt 0) {
            $message = ($errors | ForEach-Object { "$($_.Extent.File):$($_.Extent.StartLineNumber) $($_.Message)" }) -join [Environment]::NewLine
            throw "PowerShell syntax validation failed:`n$message"
        }
    }

    Write-Host "[6/7] Validating JavaScript syntax..." -ForegroundColor Cyan
    if (Get-Command node -ErrorAction SilentlyContinue) {
        Get-ChildItem .\browser-extension, .\firefox-extension -Filter *.js -File | ForEach-Object {
            node --check $_.FullName
            Assert-LastExitCode "node --check $($_.Name)"
        }
    } else {
        Write-Warning "Node.js is not installed; JavaScript syntax validation was skipped."
    }

    Write-Host "[7/7] Validating architecture invariants..." -ForegroundColor Cyan
    $duplicatePolicies = Get-ChildItem .\src -Recurse -Filter "policy*.json" -File -ErrorAction SilentlyContinue
    if ($duplicatePolicies) {
        throw "Policy files must exist only under root config. Duplicate source policy found: $($duplicatePolicies.FullName -join ', ')"
    }

    $development = Get-Content .\config\policy.development.json -Raw | ConvertFrom-Json
    if ($development.backend.mode -ne "Mock") { throw "Development backend mode must be Mock." }
    if ($development.fileClassification.provider -ne "BlockAll") { throw "Development file classification must remain BlockAll until AI integration is approved." }
    if ($development.backend.authenticationMode -ne "DevelopmentNone") { throw "Development authentication mode must be DevelopmentNone." }

    $production = Get-Content .\config\policy.production.sample.json -Raw | ConvertFrom-Json
    if ($production.backend.authenticationMode -ne "DeviceBearerToken") { throw "Production must use DeviceBearerToken." }
    if ($production.backend.allowUnsignedDevelopmentPolicy) { throw "Production must reject unsigned development policies." }
    if ($production.fileClassification.provider -ne "BlockAll") { throw "Production sample must remain BlockAll until the AI approval-token flow is approved." }
    if (-not $production.backend.baseUrl.StartsWith("https://")) { throw "Production backend URL must use HTTPS." }
    if ($production.usb.trustDevicesPresentAtFirstRun) { throw "Production must not blindly trust USB devices present at first run." }

    if (-not (Test-Path .\contracts\company-dlp-agent-api.openapi.yaml)) { throw "Backend OpenAPI contract is missing." }
    if (-not (Test-Path .\src\CompanyDlp.BrowserBridge\CompanyDlp.BrowserBridge.csproj)) { throw "BrowserBridge project is missing." }
    if (-not (Test-Path .\browser-extension\managed-storage-schema.json)) { throw "Managed extension policy schema is missing." }

    $developmentLauncher = Get-Content .\scripts\run-development.ps1 -Raw
    if ($developmentLauncher -notmatch "CompanyDlp\.Desktop\.exe") { throw "Development launcher must start CompanyDlp.Desktop.exe." }
    if ($developmentLauncher -match '&\s*\$dotnetExe\s+\$desktopDll') { throw "Development launcher must not load CompanyDlp.Desktop.dll directly through dotnet.exe." }

    $softwareMonitor = Get-Content .\src\CompanyDlp.Service\SoftwareProtectionMonitor.cs -Raw
    if ($softwareMonitor -notmatch "DevelopmentTestSessionGate\.ShouldMonitor") { throw "Development software test-session gate is missing." }
    if ($softwareMonitor -notmatch "active-test-session\.json") { throw "Development software session marker check is missing." }

    $worker = Get-Content .\browser-extension\service-worker.js -Raw
    if ($worker -notmatch "developmentHttpFallback") { throw "Development browser fallback wiring is missing." }
    if ($worker -notmatch "127\.0\.0\.1:5055/api/v1/development/native-message") { throw "Development browser fallback must remain loopback-only." }

    Write-Host "WINDOWS READY VALIDATION PASSED" -ForegroundColor Green
}
finally {
    Pop-Location
}
