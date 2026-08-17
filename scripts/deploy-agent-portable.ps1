#Requires -RunAsAdministrator
# Portable, no-rebuild agent installer - run this directly from a flash drive (or any folder) on
# any target Windows machine. Unlike install-production.ps1, this never calls publish.ps1 - it
# expects the binaries next to it to already be built AND signed (see
# scripts\build-portable-agent-package.ps1, which produces exactly this layout). That's what makes
# this safe to run on an employee's machine, which won't have the .NET SDK or a code-signing
# certificate installed.
#
# Expected layout next to this script:
#   .\publish\                          (already-built, already-signed artifacts\publish output)
#   .\Scripts\                          (copies of the register-*.ps1 helper scripts)
#   .\portable-config.json              (tenantId/backendBaseUrl/policy key/extension ids - filled
#                                         in once when the package was built)
#   .\CompanyDlpCodeSigningRoot.cer     (OPTIONAL - only present if the package was built with a
#                                         self-signed code-signing certificate instead of a real
#                                         CA-issued one; see build-portable-agent-package.ps1's
#                                         -RootCertPath. If present, this script trusts it on this
#                                         machine automatically below, since it already runs
#                                         elevated - no separate manual/GPO trust step needed.)
param(
    [string]$ConfigPath = (Join-Path $PSScriptRoot "portable-config.json"),
    # Optional: skip the interactive Read-Host prompt below by supplying the enrollment code
    # up front (e.g. for scripted/repeated installs, or if Read-Host is ever unusable in a given
    # console). Leave unset for the normal interactive flow.
    [string]$EnrollmentCode
)
$ErrorActionPreference = "Stop"

# Log everything to a file next to this script, and never let the window vanish silently on error -
# Install-CompanyDlp.bat launches this in a *separate* elevated window with no -NoExit, so an
# uncaught exception used to close that window before anyone could read what it said.
$logPath = Join-Path $PSScriptRoot "install-log.txt"
try { Start-Transcript -Path $logPath -Append -ErrorAction Stop | Out-Null } catch {
    Write-Host "Warning: could not start transcript logging to $logPath ($($_.Exception.Message))." -ForegroundColor DarkYellow
}

trap {
    Write-Host ""
    Write-Host "INSTALLATION FAILED:" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host $_.InvocationInfo.PositionMessage -ForegroundColor DarkRed
    if ($_.ScriptStackTrace) { Write-Host $_.ScriptStackTrace -ForegroundColor DarkRed }
    if ($progressForm -and -not $progressForm.IsDisposed) {
        try { $progressForm.Close(); $progressForm.Dispose() } catch {}
    }
    try { Stop-Transcript | Out-Null } catch {}
    Write-Host ""
    Write-Host "Full details were saved to: $logPath" -ForegroundColor Yellow
    Read-Host "Press Enter to close this window"
    exit 1
}

# Visible progress window - a non-technical employee double-clicking Install-CompanyDlp.bat sees a
# console window scroll by (or, worse, appear to sit there doing nothing during a slow step like
# Invoke-WithRetry or the per-file signature check below) with no indication the install is actually
# alive. This is a TopMost, non-modal WinForms window with an indeterminate ("Marquee") progress bar -
# indeterminate because install steps vary too much in duration to show real percentage - updated via
# Set-InstallProgress at every phase below. It runs on the same thread as the rest of this script (no
# background runspace); Set-InstallProgress calls [Windows.Forms.Application]::DoEvents() after every
# text update specifically so the window keeps repainting/responding between synchronous install
# steps, which is the standard, well-known way to keep a WinForms window alive from a single-threaded
# script like this one without the complexity of a second runspace.
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$progressForm = New-Object System.Windows.Forms.Form
$progressForm.Text = "Company DLP - Installing"
$progressForm.Size = New-Object System.Drawing.Size(440, 150)
$progressForm.StartPosition = "CenterScreen"
$progressForm.FormBorderStyle = "FixedDialog"
$progressForm.MaximizeBox = $false
$progressForm.MinimizeBox = $false
$progressForm.ControlBox = $false
$progressForm.TopMost = $true

$progressLabel = New-Object System.Windows.Forms.Label
$progressLabel.Text = "Starting installation..."
$progressLabel.AutoSize = $false
$progressLabel.Size = New-Object System.Drawing.Size(400, 40)
$progressLabel.Location = New-Object System.Drawing.Point(15, 15)
$progressForm.Controls.Add($progressLabel)

$progressBar = New-Object System.Windows.Forms.ProgressBar
$progressBar.Style = "Marquee"
$progressBar.MarqueeAnimationSpeed = 30
$progressBar.Location = New-Object System.Drawing.Point(15, 65)
$progressBar.Size = New-Object System.Drawing.Size(400, 25)
$progressForm.Controls.Add($progressBar)

$progressForm.Show()
$progressForm.Activate()
[System.Windows.Forms.Application]::DoEvents()

# Single choke point for "tell the employee what's happening right now" - updates both the console
# (kept for install-log.txt via Start-Transcript above) and the visible progress window in one call,
# so nothing below has to remember to do both separately.
function Set-InstallProgress {
    param([Parameter(Mandatory = $true)] [string]$Message)
    Write-Host "[step] $Message" -ForegroundColor DarkCyan
    if ($progressForm -and -not $progressForm.IsDisposed) {
        $progressLabel.Text = $Message
        $progressForm.Refresh()
        [System.Windows.Forms.Application]::DoEvents()
    }
}

# GUI fallbacks for a non-technical employee running this by double-clicking Install-CompanyDlp.bat:
# that .bat hands off to a *second*, separately-elevated PowerShell window via `Start-Process -Verb
# RunAs`, which Windows frequently leaves unfocused/behind the original window after the UAC prompt -
# a console Read-Host prompt in that second window is easy to never notice. A TopMost popup can't be
# missed regardless of which window has focus.
function Show-EnrollmentCodeDialog {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing

    $form = New-Object System.Windows.Forms.Form
    $form.Text = "Company DLP - Enrollment Code"
    $form.Size = New-Object System.Drawing.Size(420, 170)
    $form.StartPosition = "CenterScreen"
    $form.FormBorderStyle = "FixedDialog"
    $form.MaximizeBox = $false
    $form.MinimizeBox = $false
    $form.TopMost = $true

    $label = New-Object System.Windows.Forms.Label
    $label.Text = "This device needs a one-time enrollment code:"
    $label.AutoSize = $true
    $label.Location = New-Object System.Drawing.Point(15, 15)
    $form.Controls.Add($label)

    $textBox = New-Object System.Windows.Forms.TextBox
    $textBox.Location = New-Object System.Drawing.Point(15, 45)
    $textBox.Size = New-Object System.Drawing.Size(380, 25)
    $form.Controls.Add($textBox)

    $okButton = New-Object System.Windows.Forms.Button
    $okButton.Text = "OK"
    $okButton.Location = New-Object System.Drawing.Point(240, 85)
    $okButton.Size = New-Object System.Drawing.Size(75, 30)
    $okButton.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $form.Controls.Add($okButton)
    $form.AcceptButton = $okButton

    $cancelButton = New-Object System.Windows.Forms.Button
    $cancelButton.Text = "Cancel"
    $cancelButton.Location = New-Object System.Drawing.Point(320, 85)
    $cancelButton.Size = New-Object System.Drawing.Size(75, 30)
    $cancelButton.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $form.Controls.Add($cancelButton)
    $form.CancelButton = $cancelButton

    $form.Add_Shown({ $form.Activate(); $textBox.Focus() })
    $result = $form.ShowDialog()

    if ($result -eq [System.Windows.Forms.DialogResult]::OK) {
        return $textBox.Text.Trim()
    }
    return $null
}

function Show-InstallCompleteDialog {
    param([string]$Message)
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing

    # An invisible, offscreen, TopMost owner form makes the MessageBox come up on top regardless of
    # which window currently has focus - a plain MessageBox.Show() with no owner can end up behind
    # other windows the same way the old console prompt did.
    $ownerForm = New-Object System.Windows.Forms.Form
    $ownerForm.TopMost = $true
    $ownerForm.ShowInTaskbar = $false
    $ownerForm.StartPosition = "Manual"
    $ownerForm.Location = New-Object System.Drawing.Point(-2000, -2000)
    $ownerForm.Size = New-Object System.Drawing.Size(1, 1)
    $ownerForm.Show()
    [System.Windows.Forms.MessageBox]::Show($ownerForm, $Message, "Company DLP", [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Information) | Out-Null
    $ownerForm.Close()
}

# Reclaims ownership/ACLs under $Path, file by file - deliberately NOT `icacls $Path /grant ... /T`.
# Confirmed live on a real failure (not theoretical), two compounding icacls quirks:
#   1) /T batch recursion can silently no-op on a file that already has a fully empty ACL (0 access
#      rules - happens after an interrupted previous run) while still printing "processed file: ..."
#      for it and reporting zero failures overall.
#   2) The (OI)(CI) inheritance flags are meant for containers (they control how children inherit) -
#      applied directly to a plain FILE that already has an empty ACL, icacls silently drops the
#      grant instead of applying it. Plain "F" with no inheritance flags works reliably on files.
# Targeting each item directly - (OI)(CI)F for directories, plain F for files - fixed the same real
# broken file every time it was tested, including after both quirks above were independently
# confirmed. This is the actual confirmed root cause of the recurring Access Denied on this project,
# not Windows Defender (checked directly: Get-MpThreatDetection/Get-MpThreat empty, Controlled
# Folder Access disabled, no ASR rules, no relevant event log entries) and not a locked file handle
# (the error is "Access is denied", not the distinct "being used by another process" message a real
# handle conflict produces).
function Repair-PermissionsRecursive {
    param([Parameter(Mandatory = $true)] [string]$Path)
    if (-not (Test-Path $Path)) { return }
    takeown /F $Path /R /D Y | Out-Null
    icacls $Path /grant "Administrators:(OI)(CI)F" /C | Out-Null
    Get-ChildItem $Path -Recurse -Force | ForEach-Object {
        if ($_.PSIsContainer) {
            icacls $_.FullName /grant "Administrators:(OI)(CI)F" /C | Out-Null
        } else {
            icacls $_.FullName /grant "Administrators:F" /C | Out-Null
        }
    }
}

# Applies the final production ACL lockdown (e.g. SYSTEM/Administrators/Users on $installDir) the
# same per-item way Repair-PermissionsRecursive above does, instead of a single bulk
# `icacls $Path /inheritance:r /grant:r ... /T /C` call. Confirmed live as a real, separate instance
# of the same icacls quirk documented above: a bulk /T recursive grant can silently no-op on
# individual files, leaving them with an *empty* DACL (inheritance already severed by /inheritance:r,
# and the explicit grant silently failed to apply) - which Windows treats as deny-everyone, not
# allow-everyone. This is what caused "Access is denied" launching CompanyDlp.ShellExtension.
# Register.exe right after this exact lockdown step ran on a freshly-copied installDir. $ContainerGrants
# needs the (OI)(CI) inheritance flags (containers only); $LeafGrants must NOT have them (files).
function Set-LockdownRecursive {
    param(
        [Parameter(Mandatory = $true)] [string]$Path,
        [Parameter(Mandatory = $true)] [string[]]$ContainerGrants,
        [Parameter(Mandatory = $true)] [string[]]$LeafGrants
    )
    if (-not (Test-Path $Path)) { return }
    icacls $Path /inheritance:r /grant:r $ContainerGrants /C | Out-Null
    Get-ChildItem $Path -Recurse -Force | ForEach-Object {
        if ($_.PSIsContainer) {
            icacls $_.FullName /inheritance:r /grant:r $ContainerGrants /C | Out-Null
        } else {
            icacls $_.FullName /inheritance:r /grant:r $LeafGrants /C | Out-Null
        }
    }
}

# Retries a filesystem write against $installDir/$dataDir, reclaiming ownership/ACLs between
# attempts - covers any other transient cause (timing, a genuine third-party lock, etc.) on top of
# the empty-ACL case Repair-PermissionsRecursive targets directly.
function Invoke-WithRetry {
    param(
        [Parameter(Mandatory = $true)] [scriptblock]$Action,
        [Parameter(Mandatory = $true)] [string]$Label,
        [Parameter(Mandatory = $true)] [string]$HealPath,
        [int]$MaxAttempts = 20,
        [int]$DelayMilliseconds = 1500
    )
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        Write-Host "  [$Label] attempt $attempt/$MaxAttempts..." -ForegroundColor DarkGray
        try {
            & $Action
            if ($attempt -gt 1) { Write-Host "$Label succeeded on attempt $attempt." -ForegroundColor Green }
            return
        } catch {
            if ($attempt -eq $MaxAttempts) {
                Write-Host "$Label failed after $MaxAttempts attempts." -ForegroundColor Red
                try {
                    $mpStatus = Get-MpComputerStatus -ErrorAction Stop
                    Write-Host "Windows Defender status at time of failure: RealTimeProtectionEnabled=$($mpStatus.RealTimeProtectionEnabled), AMRunningMode=$($mpStatus.AMRunningMode)" -ForegroundColor Yellow
                } catch { }
                throw
            }
            Write-Host "$Label attempt $attempt failed ($($_.Exception.Message)) - reclaiming permissions and retrying in $DelayMilliseconds ms..." -ForegroundColor DarkYellow
            Repair-PermissionsRecursive -Path $HealPath
            Start-Sleep -Milliseconds $DelayMilliseconds
        }
    }
}

# Self-signed builds only: trust the bundled root certificate on this machine before checking
# signatures below, so Get-AuthenticodeSignature can actually report "Valid" for it. Safe to skip
# for a real CA-issued certificate (no .cer file is bundled in that case) and safe to re-run (skips
# the import if this exact certificate is already trusted).
$rootCertPath = Join-Path $PSScriptRoot "CompanyDlpCodeSigningRoot.cer"
if (Test-Path $rootCertPath) {
    Set-InstallProgress "Trusting bundled security certificate..."
    $rootCert = Get-PfxCertificate -FilePath $rootCertPath
    $alreadyTrusted = Get-ChildItem Cert:\LocalMachine\Root | Where-Object { $_.Thumbprint -eq $rootCert.Thumbprint }
    if (-not $alreadyTrusted) {
        Import-Certificate -FilePath $rootCertPath -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
    }
    Write-Host "Certificate trusted." -ForegroundColor Green
}

if (-not (Test-Path $ConfigPath)) {
    throw "portable-config.json was not found next to this script. This flash drive was not built with scripts\build-portable-agent-package.ps1."
}
$config = Get-Content $ConfigPath -Raw | ConvertFrom-Json

# Fail loudly, not silently, on a package built before build-identity stamping existed - an old-format
# portable-config.json with no buildCommit/buildTimestampUtc must never be treated as "validated by
# this script" the same way a current one is. Rebuild the package with the current
# build-portable-agent-package.ps1 rather than trying to patch these fields in by hand.
$hasBuildCommit = ($config.PSObject.Properties.Name -contains "buildCommit") -and -not [string]::IsNullOrWhiteSpace($config.buildCommit)
$hasBuildTimestamp = ($config.PSObject.Properties.Name -contains "buildTimestampUtc") -and -not [string]::IsNullOrWhiteSpace($config.buildTimestampUtc)
if (-not $hasBuildCommit -or -not $hasBuildTimestamp) {
    throw "portable-config.json is missing buildCommit/buildTimestampUtc. This package was built with an older version of build-portable-agent-package.ps1 (before build-identity stamping was added) and cannot be verified as a known build. Rebuild the package with the current script before deploying it."
}
Set-InstallProgress "Deploying build $($config.buildCommit) built $($config.buildTimestampUtc)..."

$tenantId = [Guid]$config.tenantId
if ($tenantId -eq [Guid]::Empty) { throw "portable-config.json: tenantId must not be empty." }
$backendUri = $null
if (-not [Uri]::TryCreate($config.backendBaseUrl, [UriKind]::Absolute, [ref]$backendUri) -or $backendUri.Scheme -ne "https") {
    throw "portable-config.json: backendBaseUrl must be a valid HTTPS URL."
}
$policySigningPublicKeyPem = $config.policySigningPublicKeyPem
if ($policySigningPublicKeyPem -notmatch "BEGIN PUBLIC KEY") { throw "portable-config.json: policySigningPublicKeyPem is invalid." }

$publishRoot = Join-Path $PSScriptRoot "publish"
$scriptsRoot = Join-Path $PSScriptRoot "Scripts"
if (-not (Test-Path $publishRoot)) { throw "publish\ folder was not found next to this script - this flash drive is incomplete." }
if (-not (Test-Path $scriptsRoot)) { throw "Scripts\ folder was not found next to this script - this flash drive is incomplete." }

# Same signature check install-production.ps1 does - doesn't need the SDK, just Get-AuthenticodeSignature
# (built into PowerShell), and confirms the binaries on this flash drive are still genuinely signed
# and untampered before touching this machine at all.
Set-InstallProgress "Checking binary signatures..."
$files = Get-ChildItem $publishRoot -Recurse -File | Where-Object { $_.Extension -in @(".exe", ".dll") }
if (-not $files) { throw "No production binaries were found under $publishRoot." }
$invalid = foreach ($file in $files) {
    $signature = Get-AuthenticodeSignature $file.FullName
    if ($signature.Status -ne "Valid") {
        [PSCustomObject]@{ File = $file.FullName; Status = $signature.Status; Message = $signature.StatusMessage }
    }
}
if ($invalid) {
    $invalid | Format-Table -AutoSize -Wrap
    throw "Stopped: every EXE and DLL on this flash drive must have a valid trusted Authenticode signature."
}
Write-Host "Signatures OK." -ForegroundColor Green

# A flash drive copied from a network share or downloaded as a zip can carry a Zone.Identifier
# Mark-of-the-Web tag on every file; Copy-Item below would propagate that tag onto every file it
# writes into Program Files, making Windows treat each freshly-copied file as if it just came from
# the internet. Stripping it from the source here (safe - doesn't touch file content or the
# Authenticode signature just verified above) means the copies never carry it either.
Set-InstallProgress "Removing Mark-of-the-Web from source files..."
Get-ChildItem $publishRoot -Recurse -File | Unblock-File -ErrorAction SilentlyContinue

$installDir = Join-Path $env:ProgramFiles "CompanyDlp"
$dataDir = Join-Path $env:ProgramData "CompanyDlp"

Set-InstallProgress "Stopping any running Company DLP processes..."
Get-Process -Name @("CompanyDlp.Desktop", "CompanyDlp.NativeHost", "CompanyDlp.Service") -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Set-InstallProgress "Checking for a previous installation..."
if (Get-Service CompanyDlp -ErrorAction SilentlyContinue) {
    Stop-Service CompanyDlp -Force -ErrorAction SilentlyContinue
    sc.exe delete CompanyDlp | Out-Null
    Start-Sleep -Seconds 1
}

# Self-heal a previous run's leftovers: line ~108 below locks $installDir/$dataDir down to
# Users:RX, so a prior run that stopped partway (or was cleaned up by hand) can leave files an
# admin can't overwrite without first reclaiming ownership - do that automatically here instead of
# requiring manual takeown/icacls before every re-run.
foreach ($existingPath in @($installDir, $dataDir)) {
    Set-InstallProgress "Cleaning up files from a previous install..."
    Repair-PermissionsRecursive -Path $existingPath
}

Set-InstallProgress "Removing the previous installation..."
Invoke-WithRetry -Label "Remove-Item ($installDir)" -HealPath $installDir -Action {
    if (Test-Path $installDir) { Remove-Item $installDir -Recurse -Force -ErrorAction Stop }
}
New-Item $installDir -ItemType Directory -Force | Out-Null
New-Item $dataDir -ItemType Directory -Force | Out-Null
Set-InstallProgress "Copying program files..."
Invoke-WithRetry -Label "Copy-Item (publish -> $installDir)" -HealPath $installDir -Action {
    Copy-Item (Join-Path $publishRoot "*") $installDir -Recurse -Force -ErrorAction Stop
}

$policyTemplatePath = Join-Path $publishRoot "policy.production.sample.json"
if (-not (Test-Path $policyTemplatePath)) {
    throw "policy.production.sample.json was not found under publish\ - this flash drive is incomplete."
}
$policy = Get-Content $policyTemplatePath -Raw | ConvertFrom-Json
$policy.browser.chromeExtensionId = $config.chromeExtensionId
$policy.browser.chromeExtensionUpdateUrl = $config.chromeExtensionUpdateUrl
$policy.browser.edgeExtensionId = $config.edgeExtensionId
$policy.browser.edgeExtensionUpdateUrl = $config.edgeExtensionUpdateUrl
$policy.backend.tenantId = $tenantId
$policy.backend.baseUrl = $backendUri.AbsoluteUri.TrimEnd('/')
$policy.backend.policySigningPublicKeyPem = $policySigningPublicKeyPem
$policy.backend.authenticationMode = "DeviceBearerToken"

# Add-Member -Force (not dot-assignment) so this still works even against a policy.production.sample.json
# template that predates these two fields - dot-assignment throws on a PSCustomObject property that
# doesn't already exist, and this script must never fail to deploy just because the bundled template is
# older than expected. This is how CompanyDlp.Service.exe --version and its startup log line find out
# which build is actually running (see BuildIdentity.cs).
$policy.backend | Add-Member -NotePropertyName "buildCommit" -NotePropertyValue $config.buildCommit -Force
$policy.backend | Add-Member -NotePropertyName "buildTimestampUtc" -NotePropertyValue $config.buildTimestampUtc -Force

Set-InstallProgress "Writing device policy..."
Invoke-WithRetry -Label "Set-Content (policy.json)" -HealPath $dataDir -Action {
    Write-Host "    [substep] Serializing policy object to JSON..." -ForegroundColor DarkGray
    $policyJsonText = $policy | ConvertTo-Json -Depth 20
    Write-Host "    [substep] Serialized ($($policyJsonText.Length) chars). Writing to disk..." -ForegroundColor DarkGray
    [System.IO.File]::WriteAllText((Join-Path $dataDir "policy.json"), $policyJsonText, [System.Text.Encoding]::UTF8)
    Write-Host "    [substep] Write complete." -ForegroundColor DarkGray
}

Set-InstallProgress "Locking down file permissions..."
Set-LockdownRecursive -Path $installDir -ContainerGrants @("SYSTEM:(OI)(CI)F", "Administrators:(OI)(CI)F", "Users:(OI)(CI)RX") -LeafGrants @("SYSTEM:F", "Administrators:F", "Users:RX")
Set-LockdownRecursive -Path $dataDir -ContainerGrants @("SYSTEM:(OI)(CI)F", "Administrators:(OI)(CI)F") -LeafGrants @("SYSTEM:F", "Administrators:F")

$serviceExe = Join-Path $installDir "Service\CompanyDlp.Service.exe"
Set-InstallProgress "Registering the background service..."
sc.exe create CompanyDlp binPath= "`"$serviceExe`"" start= auto DisplayName= "Company DLP Service" | Out-Null
sc.exe description CompanyDlp "Company endpoint data loss prevention service" | Out-Null

$desktopExe = Join-Path $installDir "Desktop\CompanyDlp.Desktop.exe"

Set-InstallProgress "Registering right-click menu integration..."
& (Join-Path $scriptsRoot "register-production-context-menu.ps1") -DesktopExe $desktopExe
Set-InstallProgress "Registering shell extension..."
& (Join-Path $scriptsRoot "register-shell-extension-production.ps1") -RegisterExe (Join-Path $installDir "ShellExtension\CompanyDlp.ShellExtension.Register.exe")
Set-InstallProgress "Registering browser integration..."
& (Join-Path $scriptsRoot "register-native-host-production.ps1") -NativeHostExe (Join-Path $installDir "NativeHost\CompanyDlp.NativeHost.exe") -ExtensionIds @($config.chromeExtensionId, $config.edgeExtensionId)
Set-InstallProgress "Registering browser extension force-install policy..."
& (Join-Path $scriptsRoot "register-browser-force-install.ps1") -ChromeExtensionId $config.chromeExtensionId -ChromeExtensionUpdateUrl $config.chromeExtensionUpdateUrl -EdgeExtensionId $config.edgeExtensionId -EdgeExtensionUpdateUrl $config.edgeExtensionUpdateUrl

# [Environment]::SetEnvironmentVariable(..., "Machine") only writes to the registry - it does NOT
# update this already-running PowerShell process's own environment block, so the `& $serviceExe
# --enroll` call a few lines below (launched as a CHILD of this same process) would NOT see these
# and would silently fall back to the exe's dev defaults (policy.development.json, localhost:7008) -
# confirmed live: --enroll tried to reach https://localhost:7008 and loaded a dev policy from the
# source repo path instead of the real production policy.json we just wrote to $dataDir. Start-Service
# below is unaffected (Service Control Manager starts a genuinely fresh process that reads the
# machine environment from the registry at that point), but --enroll needs the current-process $env:
# set explicitly too.
$policyJsonPath = Join-Path $dataDir "policy.json"
[Environment]::SetEnvironmentVariable("COMPANY_DLP_MODE", "Production", "Machine")
[Environment]::SetEnvironmentVariable("COMPANY_DLP_POLICY_PATH", $policyJsonPath, "Machine")
[Environment]::SetEnvironmentVariable("COMPANY_DLP_SESSION_AGENT_EXE", $desktopExe, "Machine")
$env:COMPANY_DLP_MODE = "Production"
$env:COMPANY_DLP_POLICY_PATH = $policyJsonPath
$env:COMPANY_DLP_SESSION_AGENT_EXE = $desktopExe

Write-Host ""
if (-not [string]::IsNullOrWhiteSpace($EnrollmentCode)) {
    Write-Host "Using enrollment code supplied via -EnrollmentCode." -ForegroundColor Green
    $enrollmentCode = $EnrollmentCode
} else {
    Write-Host "Files installed. This device now needs a one-time enrollment code (create one per device," -ForegroundColor Cyan
    Write-Host "or reuse a multi-use batch code, from the admin portal / POST /api/v1/device-enrollment-tokens)." -ForegroundColor Cyan
    Write-Host "A popup window will now ask for it - it may appear behind this window." -ForegroundColor Cyan
    # Hidden (not disposed) while the enrollment-code dialog is up - both are TopMost, and the
    # progress window sitting behind/beside the modal input dialog is just visual clutter with
    # nothing actually happening while it waits on Read-Host-equivalent user input. Shown again right
    # after for the remaining enroll/start-service steps.
    if (-not $progressForm.IsDisposed) { $progressForm.Hide() }
    $enrollmentCode = Show-EnrollmentCodeDialog
    if (-not $progressForm.IsDisposed) { $progressForm.Show(); $progressForm.Activate() }
}
if ([string]::IsNullOrWhiteSpace($enrollmentCode)) { throw "An enrollment code is required to finish setup - re-run this script once you have one." }

Set-InstallProgress "Enrolling this device..."
$env:COMPANY_DLP_ENROLLMENT_CODE = $enrollmentCode
try {
    & $serviceExe --enroll
} finally {
    Remove-Item Env:COMPANY_DLP_ENROLLMENT_CODE -ErrorAction SilentlyContinue
    $enrollmentCode = $null
}

Set-InstallProgress "Starting the service..."
Start-Service CompanyDlp
Write-Host ""
Write-Host "Company DLP agent installed and enrolled on this device." -ForegroundColor Green
Write-Host "Disconnect all non-input external USB devices before first real use on a client image." -ForegroundColor Yellow
if (-not $progressForm.IsDisposed) { $progressForm.Close(); $progressForm.Dispose() }
Show-InstallCompleteDialog -Message "Installation complete - you're all set.`n`nDisconnect all non-input external USB devices before first real use on a client image."
try { Stop-Transcript | Out-Null } catch {}
