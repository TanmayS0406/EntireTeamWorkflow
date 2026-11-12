<#
.SYNOPSIS
  Removes the Entire Team Workflow services, programs and shortcuts. Generated projects are never touched.

.PARAMETER RemoveData
  Also delete %ProgramData%\EntireTeamWorkflow (database, saved Azure DevOps / Slack / Teams / Azure connections, logs, API key).
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'EntireTeamWorkflow'),
    [switch]$RemoveData
)

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this script from an elevated (Administrator) PowerShell.' }

Get-Process -Name 'Etw.Desktop' -ErrorAction SilentlyContinue | Stop-Process -Force

foreach ($name in 'EtwAzure', 'EtwOrchestrator', 'EtwIntegrations', 'EtwBackend') {
    if (Get-Service -Name $name -ErrorAction SilentlyContinue) {
        Stop-Service -Name $name -Force -ErrorAction SilentlyContinue
        sc.exe delete $name | Out-Null
        Write-Host "Removed service $name"
    }
}
Start-Sleep -Seconds 2

foreach ($place in @([Environment]::GetFolderPath('CommonPrograms'), [Environment]::GetFolderPath('CommonDesktopDirectory'))) {
    $lnk = Join-Path $place 'Entire Team Workflow.lnk'
    if (Test-Path -LiteralPath $lnk) { Remove-Item -LiteralPath $lnk -Force }
}

if (Test-Path -LiteralPath $InstallDir) { Remove-Item -LiteralPath $InstallDir -Recurse -Force; Write-Host "Removed $InstallDir" }

$dataDir = Join-Path $env:ProgramData 'EntireTeamWorkflow'
if ($RemoveData -and (Test-Path -LiteralPath $dataDir)) { Remove-Item -LiteralPath $dataDir -Recurse -Force; Write-Host "Removed $dataDir" }
elseif (Test-Path -LiteralPath $dataDir) { Write-Host "Kept your data in $dataDir (use -RemoveData to delete it)." }
