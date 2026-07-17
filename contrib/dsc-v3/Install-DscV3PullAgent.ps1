<#
.SYNOPSIS
    Install the DSC v3 pull agent as a scheduled task (runs as SYSTEM on a timer).

.DESCRIPTION
    Copies pull-agent.ps1 to a stable location and registers a scheduled task
    that runs it every -IntervalMinutes. This is the "orchestrator" DSC v3 leaves
    to you when there's no Intune/Arc — Task Scheduler driving `dsc config set`.

.PARAMETER ConfigUrl
    URL of the configuration document the agent should pull and apply.

.PARAMETER Engine
    dsc | winget | auto (default) — passed through to the agent.

.PARAMETER IntervalMinutes
    How often to re-apply (drift correction). Default 30.

.PARAMETER VerifyChecksum
    Pass through -VerifyChecksum to the agent (expects <ConfigUrl>.sha256).

.PARAMETER ReportUrl
    Optional URL the agent POSTs each run's result to.

.EXAMPLE
    .\Install-DscV3PullAgent.ps1 -ConfigUrl https://dsc.h-edu.online/v3/baseline.dsc.yaml -VerifyChecksum
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ConfigUrl,
    [ValidateSet('auto', 'dsc', 'winget')] [string] $Engine = 'auto',
    [int] $IntervalMinutes = 30,
    [switch] $VerifyChecksum,
    [string] $ReportUrl = '',
    [string] $InstallRoot = "$env:ProgramData\DscV3Pull"
)

$ErrorActionPreference = 'Stop'
$taskName = 'DscV3PullAgent'

# 1. stage the agent script next to this installer
New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null
$agentSrc = Join-Path $PSScriptRoot 'pull-agent.ps1'
$agentDst = Join-Path $InstallRoot 'pull-agent.ps1'
Copy-Item -Path $agentSrc -Destination $agentDst -Force

# 2. build the agent argument list
$agentArgs = @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$agentDst`"",
    '-ConfigUrl', "`"$ConfigUrl`"", '-Engine', $Engine
)
if ($VerifyChecksum) { $agentArgs += '-VerifyChecksum' }
if ($ReportUrl)      { $agentArgs += @('-ReportUrl', "`"$ReportUrl`"") }

# 3. register the scheduled task: SYSTEM, at boot + every N minutes
$action  = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ($agentArgs -join ' ')
$trigger = New-ScheduledTaskTrigger -AtStartup
$repeat  = New-ScheduledTaskTrigger -Once -At (Get-Date).Date `
    -RepetitionInterval (New-TimeSpan -Minutes $IntervalMinutes)
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet -StartWhenAvailable `
    -DontStopOnIdleEnd -ExecutionTimeLimit (New-TimeSpan -Minutes 15) -MultipleInstances IgnoreNew

Register-ScheduledTask -TaskName $taskName -Action $action `
    -Trigger @($trigger, $repeat) -Principal $principal -Settings $settings -Force | Out-Null

Write-Host "[+] Registered scheduled task '$taskName'" -ForegroundColor Green
Write-Host "    engine=$Engine  interval=${IntervalMinutes}m  config=$ConfigUrl"
Write-Host "    agent: $agentDst"
Write-Host "    logs:  $InstallRoot\pull-agent.log"
Write-Host ""
Write-Host "Run once now to test:" -ForegroundColor Yellow
Write-Host "    Start-ScheduledTask -TaskName $taskName ; Get-Content '$InstallRoot\pull-agent.log' -Tail 20"
