param(
    [Parameter(Mandatory = $true)]
    [string]$ActionKey,

    [Parameter(ParameterSetName = "ByName", Mandatory = $true)]
    [string]$WindowsUser,

    [Parameter(ParameterSetName = "BySid", Mandatory = $true)]
    [string]$UserSid,

    [ValidateSet("Allow", "Deny")]
    [string]$Decision = "Allow",

    [string]$Reason = "Permanent user-specific permission",

    [string]$GrantedBy = "",

    [int]$Priority = 500,

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


if ([string]::IsNullOrWhiteSpace($ActionKey)) {
    throw "ActionKey is required."
}

$policyPath = Resolve-CompanyDlpPolicyPath -ProjectRoot $ProjectRoot
$resolvedSid = Resolve-CompanyDlpUserSid `
    -WindowsUser $WindowsUser `
    -ExplicitSid $UserSid

if ([string]::IsNullOrWhiteSpace($GrantedBy)) {
    $GrantedBy = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
}

$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json

if ($null -eq $policy.permissions) {
    throw "The policy does not contain a permissions section."
}

if ($null -eq $policy.permissions.grants) {
    $policy.permissions | Add-Member `
        -MemberType NoteProperty `
        -Name "grants" `
        -Value @()
}

$knownProperty = $policy.permissions.defaultPermissions.PSObject.Properties[$ActionKey]
if ($null -eq $knownProperty) {
    Write-Warning "Action '$ActionKey' is not present in defaultPermissions. The grant will still be added, but the corresponding protection component must support this action."
}

$now = [DateTimeOffset]::UtcNow
$revokedCount = 0

foreach ($existing in @($policy.permissions.grants)) {
    $sameAction = [string]$existing.actionKey -eq $ActionKey
    $sameType = [string]$existing.subjectType -eq "UserSid"
    $sameSid = [string]$existing.subjectId -eq $resolvedSid
    $isPermanent = [string]$existing.source -eq "PermanentPolicy"
    $isActive = $null -eq $existing.revokedAtUtc -or [string]::IsNullOrWhiteSpace([string]$existing.revokedAtUtc)

    if ($sameAction -and $sameType -and $sameSid -and $isPermanent -and $isActive) {
        $existing.revokedAtUtc = $now.ToString("O")
        $existing.revokedBy = $GrantedBy
        $revokedCount++
    }
}

$grantId = [Guid]::NewGuid()

$newGrant = [PSCustomObject]@{
    grantId = $grantId.ToString("D")
    actionKey = $ActionKey
    allowed = ($Decision -eq "Allow")
    subjectType = "UserSid"
    subjectId = $resolvedSid
    source = "PermanentPolicy"
    priority = $Priority
    startsAtUtc = $now.ToString("O")
    expiresAtUtc = $null
    reason = $Reason
    grantedBy = $GrantedBy
    createdAtUtc = $now.ToString("O")
    revokedAtUtc = $null
    revokedBy = ""
}

$policy.permissions.grants = @($policy.permissions.grants) + $newGrant

$backup = Write-CompanyDlpPolicyAtomically `
    -Path $policyPath `
    -Policy $policy `
    -BackupSuffix "before-permanent-grant"

Write-Host ""
Write-Host "Permanent permission saved." -ForegroundColor Green
Write-Host ("Grant ID : {0}" -f $grantId)
Write-Host ("Action   : {0}" -f $ActionKey)
Write-Host ("Decision : {0}" -f $Decision)
Write-Host ("User SID : {0}" -f $resolvedSid)
Write-Host ("Expires  : Never (until revoked by an administrator)")
Write-Host ("Replaced : {0} previous active permanent grant(s)" -f $revokedCount)
Write-Host ("Backup   : {0}" -f $backup) -ForegroundColor DarkGray
Write-Host ""
Write-Host "The Windows agent should reload the policy automatically. Wait 10-15 seconds before testing." -ForegroundColor Cyan
