param(
    [Parameter(ParameterSetName = "ByGrant", Mandatory = $true)]
    [Guid]$GrantId,

    [Parameter(ParameterSetName = "ByUserAction", Mandatory = $true)]
    [string]$ActionKey,

    [Parameter(ParameterSetName = "ByUserAction", Mandatory = $true)]
    [string]$WindowsUser,

    [Parameter(ParameterSetName = "BySidAction", Mandatory = $true)]
    [string]$SidActionKey,

    [Parameter(ParameterSetName = "BySidAction", Mandatory = $true)]
    [string]$UserSid,

    [string]$RevokedBy = "",

    [string]$ProjectRoot = "."
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = "Stop"


function Resolve-CompanyDlpUserSid {
    param(
        [string]$WindowsUser,
        [string]$ExplicitSid
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitSid)) {
        if ($ExplicitSid -notmatch '^S-\d-\d+(-\d+)+$') {
            throw "Invalid Windows SID: $ExplicitSid"
        }

        return $ExplicitSid.Trim()
    }

    if ([string]::IsNullOrWhiteSpace($WindowsUser)) {
        throw "WindowsUser or UserSid is required."
    }

    $candidates = New-Object System.Collections.Generic.List[string]

    if ($WindowsUser.Contains('\')) {
        $candidates.Add($WindowsUser)
    }
    else {
        $candidates.Add("$env:COMPUTERNAME\$WindowsUser")
        $candidates.Add($WindowsUser)
    }

    foreach ($candidate in $candidates) {
        try {
            $account = New-Object System.Security.Principal.NTAccount($candidate)
            return $account.Translate(
                [System.Security.Principal.SecurityIdentifier]
            ).Value
        }
        catch {
            # Try the next candidate.
        }
    }

    throw "Could not resolve Windows user '$WindowsUser' to a SID."
}

function Resolve-CompanyDlpPolicyPath {
    param([string]$ProjectRoot)

    $root = (Resolve-Path -LiteralPath $ProjectRoot).Path
    $path = Join-Path $root "config\policy.development.json"

    if (-not (Test-Path -LiteralPath $path)) {
        throw "Development policy was not found: $path"
    }

    return $path
}

function Write-CompanyDlpPolicyAtomically {
    param(
        [string]$Path,
        [object]$Policy,
        [string]$BackupSuffix
    )

    $directory = Split-Path -Parent $Path
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $backupPath = "$Path.$BackupSuffix-$timestamp.bak"
    $tempPath = Join-Path $directory (
        ".{0}.{1}.tmp" -f [System.IO.Path]::GetFileName($Path), [Guid]::NewGuid().ToString("N")
    )

    Copy-Item -LiteralPath $Path -Destination $backupPath -Force

    $json = $Policy | ConvertTo-Json -Depth 100
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($tempPath, $json, $utf8)

    try {
        Move-Item -LiteralPath $tempPath -Destination $Path -Force
    }
    finally {
        Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
    }

    return $backupPath
}


$policyPath = Resolve-CompanyDlpPolicyPath -ProjectRoot $ProjectRoot
$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json

if ([string]::IsNullOrWhiteSpace($RevokedBy)) {
    $RevokedBy = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
}

$resolvedSid = $null
$resolvedAction = $null

if ($PSCmdlet.ParameterSetName -eq "ByUserAction") {
    $resolvedSid = Resolve-CompanyDlpUserSid -WindowsUser $WindowsUser -ExplicitSid ""
    $resolvedAction = $ActionKey
}
elseif ($PSCmdlet.ParameterSetName -eq "BySidAction") {
    $resolvedSid = Resolve-CompanyDlpUserSid -WindowsUser "" -ExplicitSid $UserSid
    $resolvedAction = $SidActionKey
}

$now = [DateTimeOffset]::UtcNow
$matches = @()

foreach ($grant in @($policy.permissions.grants)) {
    $isPermanent = [string]$grant.source -eq "PermanentPolicy"
    $isActive = $null -eq $grant.revokedAtUtc -or [string]::IsNullOrWhiteSpace([string]$grant.revokedAtUtc)

    if (-not $isPermanent -or -not $isActive) {
        continue
    }

    $matched = $false

    if ($PSCmdlet.ParameterSetName -eq "ByGrant") {
        $matched = [string]$grant.grantId -eq $GrantId.ToString("D")
    }
    else {
        $matched =
            [string]$grant.actionKey -eq $resolvedAction -and
            [string]$grant.subjectType -eq "UserSid" -and
            [string]$grant.subjectId -eq $resolvedSid
    }

    if ($matched) {
        $grant.revokedAtUtc = $now.ToString("O")
        $grant.revokedBy = $RevokedBy
        $matches += $grant
    }
}

if ($matches.Count -eq 0) {
    throw "No active permanent permission matched the supplied criteria."
}

$backup = Write-CompanyDlpPolicyAtomically `
    -Path $policyPath `
    -Policy $policy `
    -BackupSuffix "before-permanent-revoke"

Write-Host ""
Write-Host ("Revoked {0} permanent permission(s)." -f $matches.Count) -ForegroundColor Green

foreach ($grant in $matches) {
    Write-Host ("Grant ID : {0}" -f $grant.grantId)
    Write-Host ("Action   : {0}" -f $grant.actionKey)
    Write-Host ("User SID : {0}" -f $grant.subjectId)
    Write-Host ("Revoked  : {0}" -f $grant.revokedAtUtc)
    Write-Host ""
}

Write-Host ("Backup   : {0}" -f $backup) -ForegroundColor DarkGray
Write-Host "Wait 10-15 seconds before retesting. The default policy will apply again." -ForegroundColor Cyan
