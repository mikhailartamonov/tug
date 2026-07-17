<#
.SYNOPSIS
    Self-hosted DSC v3 "pull" agent — fetch a configuration from an HTTP host and
    apply it locally, on a schedule. Thin glue around the stock engines
    (dsc.exe or winget configure); it invents no apply logic of its own.

.DESCRIPTION
    DSC v3 deliberately ships no pull server/agent — Microsoft expects you to
    drive `dsc config set` (or `winget configure`) from whatever orchestrator you
    have. With no Intune/Arc, that orchestrator is Task Scheduler + this script.

    The agent:
      1. downloads the config document from -ConfigUrl (TLS 1.2),
      2. verifies it against an optional SHA-256 sidecar (-ChecksumUrl),
      3. ensures the chosen engine is present (bootstraps dsc.exe if missing),
      4. applies it: `dsc config set` or `winget configure`,
      5. logs the result and optionally POSTs it back to -ReportUrl.

    Register it with Install-DscV3PullAgent.ps1.

.PARAMETER ConfigUrl
    URL of the configuration document to apply. For -Engine dsc this is a DSC v3
    document; for -Engine winget it is a WinGet Configuration file.

.PARAMETER ChecksumUrl
    Optional URL of a file whose first token is the config's uppercase SHA-256.
    Defaults to "<ConfigUrl>.sha256" if -VerifyChecksum is set and this is empty.

.PARAMETER Engine
    dsc | winget | auto (default). 'auto' prefers dsc.exe (broadest OS support),
    falling back to winget only if dsc is unavailable and winget is present.

.PARAMETER DscVersion
    dsc.exe release tag to bootstrap when missing (default: v3.2.3).

.PARAMETER InstallDir
    Where to place a bootstrapped dsc.exe (default: C:\Program Files\DSC).

.PARAMETER WorkDir
    Scratch/log directory (default: C:\ProgramData\DscV3Pull).

.PARAMETER ReportUrl
    Optional URL to POST the JSON result to after applying.

.EXAMPLE
    .\pull-agent.ps1 -ConfigUrl https://dsc.h-edu.online/v3/baseline.dsc.yaml -VerifyChecksum
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ConfigUrl,
    [string] $ChecksumUrl = '',
    [switch] $VerifyChecksum,
    [ValidateSet('auto', 'dsc', 'winget')] [string] $Engine = 'auto',
    [string] $DscVersion = 'v3.2.3',
    [string] $InstallDir = "$env:ProgramFiles\DSC",
    [string] $WorkDir = "$env:ProgramData\DscV3Pull",
    [string] $ReportUrl = '',
    # first-contact enrollment: on the very first run, export the node's current
    # state and upload it as a baseline seed (runs once, gated by a local marker).
    [string] $EnrollUrl = '',
    [string] $ExportSpecUrl = '',
    [switch] $NoEnroll
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
$logFile = Join-Path $WorkDir 'pull-agent.log'
function Log($msg) {
    $line = "$([DateTime]::UtcNow.ToString('o'))  $msg"
    Add-Content -Path $logFile -Value $line
    Write-Verbose $line
    Write-Host $line
}

# ---- resolve engine -------------------------------------------------------
function Test-Cmd($name) { [bool](Get-Command $name -ErrorAction SilentlyContinue) }

function Get-DscExe {
    if (Test-Cmd 'dsc') { return 'dsc' }
    $local = Join-Path $InstallDir 'dsc.exe'
    if (Test-Path $local) { return $local }

    # bootstrap: download the release zip and extract
    Log "dsc.exe not found; bootstrapping $DscVersion into $InstallDir"
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    $arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'aarch64' } else { 'x86_64' }
    $asset = "DSC-$($DscVersion.TrimStart('v'))-$arch-pc-windows-msvc.zip"
    $url = "https://github.com/PowerShell/DSC/releases/download/$DscVersion/$asset"
    $zip = Join-Path $WorkDir $asset
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    Expand-Archive -Path $zip -DestinationPath $InstallDir -Force
    Remove-Item $zip -Force
    if (-not (Test-Path $local)) { throw "bootstrap failed: $local not present after extract" }
    Log "dsc.exe bootstrapped: $local"
    return $local
}

$useEngine = $Engine
if ($useEngine -eq 'auto') {
    $dscPresent = (Test-Cmd 'dsc') -or (Test-Path (Join-Path $InstallDir 'dsc.exe'))
    $useEngine = if ($dscPresent) { 'dsc' }
                 elseif (Test-Cmd 'winget') { 'winget' }
                 else { 'dsc' }   # neither present -> bootstrap dsc.exe
}
Log "engine: $useEngine"

# ---- first-contact enrollment: export current state once ------------------
# On the very first run (no marker), snapshot what this machine currently is and
# upload it as a baseline seed, BEFORE applying any desired state. Best-effort:
# a failure never blocks the pull, and the marker is only written on success so
# it retries next run.
$marker = Join-Path $WorkDir 'enrolled.marker'
if (-not $NoEnroll -and $EnrollUrl -and -not (Test-Path $marker)) {
    try {
        Log "first contact: exporting current state via $useEngine"
        $snapshot = $null

        if ($useEngine -eq 'winget') {
            $exp = Join-Path $WorkDir 'export.winget.yaml'
            Remove-Item $exp -ErrorAction SilentlyContinue
            # NOTE: confirm the exact 'winget configure export' flag on a live node
            # (the --output flag is unreliable per the docs); fall back to stdout.
            $wgOut = & winget configure export --file $exp `
                --accept-configuration-agreements --disable-interactivity 2>&1 | Out-String
            if (Test-Path $exp) { $snapshot = Get-Content $exp -Raw }
            elseif ($wgOut.Trim()) { $snapshot = $wgOut }
        }
        else {
            $dsc = Get-DscExe
            $spec = Join-Path $WorkDir 'export-spec.yaml'
            if ($ExportSpecUrl) { Invoke-WebRequest -Uri $ExportSpecUrl -OutFile $spec -UseBasicParsing }
            if (Test-Path $spec) {
                $snapshot = & $dsc config export --file $spec --output-format yaml 2>&1 | Out-String
            }
            else { Log "no export spec (set -ExportSpecUrl); skipping dsc export" }
        }

        if ($snapshot -and $snapshot.Trim()) {
            Invoke-WebRequest -Uri $EnrollUrl -Method Post -Body $snapshot -ContentType 'text/yaml' `
                -Headers @{ 'X-Node' = $env:COMPUTERNAME; 'X-Engine' = $useEngine; 'X-Format' = 'yaml' } `
                -UseBasicParsing | Out-Null
            Set-Content -Path $marker -Value ([DateTime]::UtcNow.ToString('o'))
            Log "enrollment snapshot uploaded to $EnrollUrl; marker written (runs once)"
        }
        else { Log "enrollment produced no snapshot; will retry next run" }
    }
    catch { Log "enrollment failed (non-fatal, will retry next run): $_" }
}

# ---- download + verify config --------------------------------------------
$cfgPath = Join-Path $WorkDir 'config.yaml'
Log "downloading config from $ConfigUrl"
Invoke-WebRequest -Uri $ConfigUrl -OutFile $cfgPath -UseBasicParsing

if ($VerifyChecksum) {
    if (-not $ChecksumUrl) { $ChecksumUrl = "$ConfigUrl.sha256" }
    $want = ((Invoke-WebRequest -Uri $ChecksumUrl -UseBasicParsing).Content -split '\s+')[0].Trim().ToUpper()
    $got = (Get-FileHash -Path $cfgPath -Algorithm SHA256).Hash.ToUpper()
    if ($want -ne $got) { throw "checksum mismatch: want $want got $got" }
    Log "checksum OK ($got)"
}

# ---- apply ----------------------------------------------------------------
$result = $null
try {
    if ($useEngine -eq 'winget') {
        if (-not (Test-Cmd 'winget')) { throw "winget not available on this OS (Win10/11 or Server 2025 only)" }
        Log "applying via: winget configure --file $cfgPath"
        $out = & winget configure --file $cfgPath --accept-configuration-agreements --disable-interactivity 2>&1 | Out-String
        $result = @{ engine = 'winget'; exitCode = $LASTEXITCODE; output = $out }
    }
    else {
        $dsc = Get-DscExe
        Log "applying via: $dsc config set --file $cfgPath"
        $out = & $dsc config set --file $cfgPath 2>&1 | Out-String
        $result = @{ engine = 'dsc'; exitCode = $LASTEXITCODE; output = $out }
    }
    Log "apply finished (exit $($result.exitCode))"
}
catch {
    $result = @{ engine = $useEngine; exitCode = -1; output = "$_" }
    Log "apply FAILED: $_"
}

# ---- report (optional) ----------------------------------------------------
$report = [ordered]@{
    node      = $env:COMPUTERNAME
    time      = [DateTime]::UtcNow.ToString('o')
    engine    = $result.engine
    exitCode  = $result.exitCode
    success   = ($result.exitCode -eq 0)
    output    = $result.output
}
$reportJson = $report | ConvertTo-Json -Depth 5
Set-Content -Path (Join-Path $WorkDir 'last-report.json') -Value $reportJson

if ($ReportUrl) {
    try {
        Invoke-WebRequest -Uri $ReportUrl -Method Post -Body $reportJson `
            -ContentType 'application/json' -UseBasicParsing | Out-Null
        Log "report POSTed to $ReportUrl"
    } catch { Log "report POST failed (non-fatal): $_" }
}

if (-not $report.success) { exit 1 }
