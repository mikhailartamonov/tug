<#
.SYNOPSIS
    Scaffold a DSC configuration from a template and (optionally) compile it to a MOF.

.DESCRIPTION
    Writes a ready-to-edit <Name>.ps1 from one of a few starter templates, and with
    -Compile also produces <Name>.mof (cleaning the output folder first so you never
    ship a stale localhost.mof). Prints the exact tug-publish command to run on the
    pull server afterwards.

    This is a convenience wrapper, not a MOF generator - the real DSC compiler
    produces the MOF. See contrib/docs/MOF-AUTHORING.md for the full recipe.

.PARAMETER Name
    Configuration name. The compiled file must be published as <Name>.mof and the
    node's LCM must list <Name> in ConfigurationNames.

.PARAMETER Template
    Starter to scaffold: report | file | service | feature. Default: report.

.PARAMETER OutputPath
    Where to write <Name>.ps1 (and, with -Compile, <Name>.mof). Default: current dir.

.PARAMETER Compile
    Also compile the scaffolded configuration to <Name>.mof.

.EXAMPLE
    .\New-TugConfig.ps1 -Name SystemReport -Template report -Compile

.EXAMPLE
    .\New-TugConfig.ps1 -Name AppServer -Template feature
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^[A-Za-z][A-Za-z0-9_]*$')] [string] $Name,
    [ValidateSet('report','file','service','feature')] [string] $Template = 'report',
    [string] $OutputPath = '.',
    [switch] $Compile
)

$ErrorActionPreference = 'Stop'

# ---- templates (the literal {{NAME}} token is replaced with $Name) ----
$templates = @{

    report = @'
Configuration {{NAME}}
{
    Import-DscResource -ModuleName PSDesiredStateConfiguration

    Node localhost
    {
        File ReportDir {
            DestinationPath = 'C:\DSC\Reports'
            Type            = 'Directory'
            Ensure          = 'Present'
        }

        # TestScript returns $false -> Set runs every consistency check (a fresh report each time)
        Script Collect {
            DependsOn  = '[File]ReportDir'
            GetScript  = { @{ Result = 'report' } }
            TestScript = { $false }
            SetScript  = {
                $report = [ordered]@{
                    ComputerName = $env:COMPUTERNAME
                    Time         = (Get-Date).ToString('o')
                    OS           = (Get-CimInstance Win32_OperatingSystem).Caption
                    Build        = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').CurrentBuildNumber
                    PowerShell   = $PSVersionTable.PSVersion.ToString()
                }
                $report | ConvertTo-Json |
                    Out-File "C:\DSC\Reports\{{NAME}}_$(Get-Date -Format yyyyMMdd_HHmmss).json" -Encoding UTF8
            }
        }
    }
}
'@

    file = @'
Configuration {{NAME}}
{
    Import-DscResource -ModuleName PSDesiredStateConfiguration

    Node localhost
    {
        File AppDir {
            DestinationPath = 'C:\App'
            Type            = 'Directory'
            Ensure          = 'Present'
        }

        File AppReadme {
            DestinationPath = 'C:\App\README.txt'
            Contents        = "Managed by DSC configuration {{NAME}}.`n"
            Ensure          = 'Present'
            DependsOn       = '[File]AppDir'
        }
    }
}
'@

    service = @'
Configuration {{NAME}}
{
    Import-DscResource -ModuleName PSDesiredStateConfiguration

    Node localhost
    {
        # Ensure a Windows service is running and starts automatically.
        Service Spooler {
            Name        = 'Spooler'
            State       = 'Running'
            StartupType = 'Automatic'
        }
    }
}
'@

    feature = @'
Configuration {{NAME}}
{
    Import-DscResource -ModuleName PSDesiredStateConfiguration

    Node localhost
    {
        # Install a Windows role/feature (Windows Server). Adjust the Name.
        WindowsFeature WebServer {
            Name   = 'Web-Server'
            Ensure = 'Present'
        }
    }
}
'@
}

# ---- write the .ps1 ----
$null = New-Item -ItemType Directory -Force -Path $OutputPath
$ps1Path = Join-Path $OutputPath "$Name.ps1"
if (Test-Path $ps1Path) {
    throw "$ps1Path already exists - choose another -Name or remove it first."
}
$body = $templates[$Template].Replace('{{NAME}}', $Name)
Set-Content -Path $ps1Path -Value $body -Encoding UTF8
Write-Host "scaffolded  $ps1Path  (template: $Template)" -ForegroundColor Green

if (-not $Compile) {
    Write-Host "`nEdit it, then compile with:" -ForegroundColor Yellow
    Write-Host "  .\New-TugConfig.ps1 -Name $Name -Compile   # (or dot-source + run it yourself)"
    return
}

# ---- compile to <Name>.mof (clean output dir first) ----
$outDir = Join-Path $OutputPath ".tugbuild_$Name"
Remove-Item -Recurse -Force $outDir -ErrorAction SilentlyContinue
$null = New-Item -ItemType Directory -Force -Path $outDir

. $ps1Path
& $Name -OutputPath $outDir | Out-Null

$compiled = Join-Path $outDir 'localhost.mof'
if (-not (Test-Path $compiled)) { throw "compilation produced no localhost.mof" }

$mofPath = Join-Path $OutputPath "$Name.mof"
Move-Item -Force $compiled $mofPath
Remove-Item -Recurse -Force $outDir -ErrorAction SilentlyContinue

$hash = (Get-FileHash $mofPath -Algorithm SHA256).Hash
$size = (Get-Item $mofPath).Length
Write-Host "compiled    $mofPath  ($size bytes)" -ForegroundColor Green
Write-Host "  SHA-256   $hash" -ForegroundColor Cyan

Write-Host "`nNext - copy $Name.mof to the pull server and publish it:" -ForegroundColor Yellow
Write-Host "  tug-publish $Name.mof $Name            # deploys both paths + checksum"
Write-Host "  tug-publish $Name.mof $Name --verify   # ...and runs the protocol harness"
