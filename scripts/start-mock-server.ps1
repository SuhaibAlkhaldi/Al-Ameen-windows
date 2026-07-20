$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$env:COMPANY_DLP_MODE = "Development"
$env:COMPANY_DLP_PROJECT_ROOT = $root
$env:COMPANY_DLP_POLICY_PATH = Join-Path $root "config\policy.development.json"
$env:COMPANY_DLP_MOCK_URL = "http://127.0.0.1:5055"
$env:COMPANY_DLP_MOCK_DATA = Join-Path $root ".development\mock-server"
$dotnetExe = (Get-Command dotnet -ErrorAction Stop).Source
$mockServerDll = Join-Path $root "src\CompanyDlp.MockServer\bin\Debug\net8.0-windows\CompanyDlp.MockServer.dll"

Push-Location $root
try {
    if (-not (Test-Path -LiteralPath $mockServerDll)) {
        dotnet build .\src\CompanyDlp.MockServer\CompanyDlp.MockServer.csproj --configuration Debug
        if ($LASTEXITCODE -ne 0) { throw "Mock Server build failed." }
    }

    & $dotnetExe $mockServerDll
    if ($LASTEXITCODE -ne 0) { throw "Mock Server exited with code $LASTEXITCODE." }
}
finally {
    Pop-Location
}
