[CmdletBinding()]
param(
    [string]$ProjectRoot = (Get-Location).Path,
    [switch]$Build
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$agentFile = Join-Path $ProjectRoot 'src\CompanyDlp.AdminApi\Endpoints\AgentEndpoints.cs'
$adminFile = Join-Path $ProjectRoot 'src\CompanyDlp.AdminApi\Endpoints\AdminManagementEndpoints.cs'

foreach ($path in @($agentFile, $adminFile)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Expected source file was not found: $path"
    }
}

$agentText = Get-Content -LiteralPath $agentFile -Raw
$oldAgent = @'
        var duplicateIds = await db.SecurityEvents.AsNoTracking()
            .Where(value => value.TenantId == agent.TenantId && candidateIds.Contains(value.EventId))
            .Select(value => value.EventId)
            .ToHashSetAsync(ct);
'@
$newAgent = @'
        var duplicateIds = (await db.SecurityEvents.AsNoTracking()
            .Where(value => value.TenantId == agent.TenantId && candidateIds.Contains(value.EventId))
            .Select(value => value.EventId)
            .ToListAsync(ct))
            .ToHashSet();
'@

if ($agentText.Contains($oldAgent)) {
    Copy-Item -LiteralPath $agentFile -Destination "$agentFile.v1.1.0.bak" -Force
    Set-Content -LiteralPath $agentFile -Value $agentText.Replace($oldAgent, $newAgent) -Encoding UTF8
    Write-Host 'Patched AgentEndpoints.cs' -ForegroundColor Green
}
elseif ($agentText.Contains('.ToListAsync(ct))') -and $agentText.Contains('.ToHashSet();')) {
    Write-Host 'AgentEndpoints.cs is already patched.' -ForegroundColor Yellow
}
else {
    throw 'The expected ToHashSetAsync block was not found. The file may differ from v1.1.0.'
}

$adminText = Get-Content -LiteralPath $adminFile -Raw
$oldAdmin = '        var scopeType = request.ScopeType.Trim();'
$newAdmin = '        var scopeType = request.ScopeType?.Trim() ?? string.Empty;'

if ($adminText.Contains($oldAdmin)) {
    Copy-Item -LiteralPath $adminFile -Destination "$adminFile.v1.1.0.bak" -Force
    Set-Content -LiteralPath $adminFile -Value $adminText.Replace($oldAdmin, $newAdmin) -Encoding UTF8
    Write-Host 'Patched AdminManagementEndpoints.cs' -ForegroundColor Green
}
elseif ($adminText.Contains($newAdmin)) {
    Write-Host 'AdminManagementEndpoints.cs is already patched.' -ForegroundColor Yellow
}
else {
    throw 'The expected ScopeType line was not found. The file may differ from v1.1.0.'
}

Write-Host 'Company DLP v1.1.1 build hotfix applied.' -ForegroundColor Cyan

if ($Build) {
    & dotnet build (Join-Path $ProjectRoot 'CompanyDlp.sln') --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }
}
