param(
    [switch]$SkipShellIntegration
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$service = $null
$mockServer = $null
$transcriptStarted = $false
$logRoot = Join-Path $root ".development\logs"
$launcherLog = Join-Path $logRoot "launcher.log"
$launcherErrorLog = Join-Path $logRoot "launcher.error.log"

New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
Remove-Item $launcherLog, $launcherErrorLog -Force -ErrorAction SilentlyContinue

function Write-LauncherStep {
    param([Parameter(Mandatory = $true)][string]$Message)

    $line = "[{0:yyyy-MM-dd HH:mm:ss.fff}] {1}" -f (Get-Date), $Message
    Write-Host $line -ForegroundColor Cyan
    Add-Content -LiteralPath $launcherLog -Value $line -Encoding UTF8
}

try {
    Start-Transcript -Path (Join-Path $logRoot "launcher.transcript.log") -Force | Out-Null
    $transcriptStarted = $true

    Write-LauncherStep "Restoring any previous Development session."
    & (Join-Path $PSScriptRoot "restore-development.ps1")

    Push-Location $root
    try {
        Write-LauncherStep "Building CompanyDlp.sln in Debug configuration."
        dotnet build .\CompanyDlp.sln --configuration Debug
        if ($LASTEXITCODE -ne 0) {
            throw "Build failed with exit code $LASTEXITCODE."
        }
        Write-LauncherStep "Build completed successfully. Preparing runtime files."

        $nativeHost = Join-Path $root "src\CompanyDlp.NativeHost\bin\Debug\net8.0-windows\CompanyDlp.NativeHost.exe"
        $desktopExe = Join-Path $root "src\CompanyDlp.Desktop\bin\Debug\net8.0-windows\CompanyDlp.Desktop.exe"
        $serviceDll = Join-Path $root "src\CompanyDlp.Service\bin\Debug\net8.0-windows\CompanyDlp.Service.dll"
        $mockServerDll = Join-Path $root "src\CompanyDlp.MockServer\bin\Debug\net8.0-windows\CompanyDlp.MockServer.dll"
        $policy = Join-Path $root "config\policy.development.json"
        $dotnetExe = (Get-Command dotnet -ErrorAction Stop).Source

        foreach ($requiredFile in @($desktopExe, $serviceDll, $mockServerDll, $policy, $dotnetExe)) {
            if (-not (Test-Path -LiteralPath $requiredFile)) {
                throw "Required development file was not found: $requiredFile"
            }
        }
        Write-LauncherStep "All required runtime files were found."

        $env:COMPANY_DLP_MODE = "Development"
        $env:COMPANY_DLP_POLICY_PATH = $policy
        $env:COMPANY_DLP_PROJECT_ROOT = $root
        $env:COMPANY_DLP_NATIVE_HOST_EXE = $nativeHost
        $env:COMPANY_DLP_MOCK_URL = "http://127.0.0.1:5055"
        $env:COMPANY_DLP_MOCK_DATA = Join-Path $root ".development\mock-server"

        $mockOut = Join-Path $logRoot "mock-server.stdout.log"
        $mockErr = Join-Path $logRoot "mock-server.stderr.log"
        $serviceOut = Join-Path $logRoot "service.stdout.log"
        $serviceErr = Join-Path $logRoot "service.stderr.log"
        Remove-Item $mockOut, $mockErr, $serviceOut, $serviceErr -Force -ErrorAction SilentlyContinue

        Write-LauncherStep "Starting Development Mock Server through signed dotnet.exe."
        $mockServer = Start-Process -FilePath $dotnetExe `
            -ArgumentList ('"{0}"' -f $mockServerDll) `
            -WorkingDirectory $root `
            -RedirectStandardOutput $mockOut `
            -RedirectStandardError $mockErr `
            -PassThru

        $mockReady = $false
        for ($attempt = 1; $attempt -le 40; $attempt++) {
            if ($mockServer.HasExited) {
                $errorText = if (Test-Path $mockErr) { Get-Content $mockErr -Raw -ErrorAction SilentlyContinue } else { "" }
                $outputText = if (Test-Path $mockOut) { Get-Content $mockOut -Raw -ErrorAction SilentlyContinue } else { "" }
                throw "Development Mock Server exited with code $($mockServer.ExitCode).`nSTDERR:`n$errorText`nSTDOUT:`n$outputText"
            }

            try {
                $health = Invoke-RestMethod -Uri "http://127.0.0.1:5055/health" -TimeoutSec 1
                if ($health.status -eq "Healthy") {
                    $mockReady = $true
                    break
                }
            }
            catch {
                Start-Sleep -Milliseconds 250
            }
        }

        if (-not $mockReady) {
            throw "Development Mock Server did not become ready. Review $mockOut and $mockErr."
        }
        Write-LauncherStep "Development Mock Server is healthy at http://127.0.0.1:5055 (PID $($mockServer.Id))."

        Write-LauncherStep "Starting Company DLP Service through signed dotnet.exe."
        $service = Start-Process -FilePath $dotnetExe `
            -ArgumentList ('"{0}"' -f $serviceDll) `
            -WorkingDirectory $root `
            -RedirectStandardOutput $serviceOut `
            -RedirectStandardError $serviceErr `
            -PassThru

        Start-Sleep -Seconds 3
        if ($service.HasExited) {
            $errorText = if (Test-Path $serviceErr) { Get-Content $serviceErr -Raw -ErrorAction SilentlyContinue } else { "" }
            $outputText = if (Test-Path $serviceOut) { Get-Content $serviceOut -Raw -ErrorAction SilentlyContinue } else { "" }
            throw "Company DLP Service exited with code $($service.ExitCode).`nSTDERR:`n$errorText`nSTDOUT:`n$outputText"
        }
        Write-LauncherStep "Company DLP Service is running (PID $($service.Id))."

        if ($SkipShellIntegration) {
            Write-LauncherStep "Skipping Development File Explorer integration by request."
        }
        else {
            Write-LauncherStep "Registering Development File Explorer context-menu actions."
            & (Join-Path $PSScriptRoot "register-development-context-menu.ps1") `
                -DesktopExe $desktopExe
            Write-LauncherStep "Development File Explorer context-menu actions registered."
        }

        Write-LauncherStep "Starting Company DLP Desktop through its Windows apphost executable."
        $desktopProcess = Start-Process -FilePath $desktopExe `
            -WorkingDirectory (Split-Path -Parent $desktopExe) `
            -PassThru `
            -Wait
        if ($desktopProcess.ExitCode -ne 0) {
            throw "Company DLP Desktop exited with code $($desktopProcess.ExitCode)."
        }
        Write-LauncherStep "Company DLP Desktop closed normally."
    }
    finally {
        Pop-Location -ErrorAction SilentlyContinue
    }
}
catch {
    $details = ($_ | Format-List * -Force | Out-String)
    $message = "Company DLP Development startup failed.`r`n$details"
    Set-Content -LiteralPath $launcherErrorLog -Value $message -Encoding UTF8
    Write-Host "`nCOMPANY DLP DEVELOPMENT STARTUP FAILED" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host "Detailed error: $launcherErrorLog" -ForegroundColor Yellow
    Write-Host "Launcher trace: $launcherLog" -ForegroundColor Yellow
    exit 1
}
finally {
    if ($service -and -not $service.HasExited) {
        Stop-Process -Id $service.Id -Force -ErrorAction SilentlyContinue
        Wait-Process -Id $service.Id -Timeout 5 -ErrorAction SilentlyContinue
    }
    if ($mockServer -and -not $mockServer.HasExited) {
        Stop-Process -Id $mockServer.Id -Force -ErrorAction SilentlyContinue
        Wait-Process -Id $mockServer.Id -Timeout 5 -ErrorAction SilentlyContinue
    }

    Write-LauncherStep "Restoring Development changes."
    & (Join-Path $PSScriptRoot "restore-development.ps1")

    if ($transcriptStarted) {
        Stop-Transcript -ErrorAction SilentlyContinue | Out-Null
    }
}
