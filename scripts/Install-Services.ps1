<#
.SYNOPSIS
  Installs Entire Team Workflow: four Windows services (backend + three microservices) and the desktop app.

.DESCRIPTION
  Run from an elevated PowerShell. By default the services run as YOUR Windows account, because the agents use the Claude
  sign-in stored in your profile (%USERPROFILE%\.claude). Running them as LocalSystem would give them no Claude sign-in.

.PARAMETER InstallDir
  Where the programs are copied. Default: C:\Program Files\EntireTeamWorkflow

.PARAMETER ServiceUser
  Account the services run as. Default: the current user (DOMAIN\name). You are asked for its password.

.PARAMETER LocalSystem
  Run the services as LocalSystem instead (no password). Claude sign-in will NOT be available to the agents.

.PARAMETER SourceDir
  A folder produced by Publish-All.ps1. Default: <repo>\dist (published first when missing).

.PARAMETER SelfContained
  Passed to Publish-All.ps1 when it has to publish.
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'EntireTeamWorkflow'),
    [string]$ServiceUser = "$env:USERDOMAIN\$env:USERNAME",
    [switch]$LocalSystem,
    [string]$SourceDir,
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this script from an elevated (Administrator) PowerShell.' }

$root = Split-Path -Parent $PSScriptRoot
if (-not $SourceDir) { $SourceDir = Join-Path $root 'dist' }
$dataDir = Join-Path $env:ProgramData 'EntireTeamWorkflow'

$services = @(
    @{ Name = 'EtwBackend';      Folder = 'Backend';      Exe = 'Etw.Backend.exe';      Display = 'Entire Team Workflow - Backend';      Port = 5180; Text = 'API, database and live updates for the Entire Team Workflow desktop app.' },
    @{ Name = 'EtwIntegrations'; Folder = 'Integrations'; Exe = 'Etw.Integrations.exe'; Display = 'Entire Team Workflow - Integrations'; Port = 5181; Text = 'Azure DevOps, Slack and Teams integrations.' },
    @{ Name = 'EtwOrchestrator'; Folder = 'Orchestrator'; Exe = 'Etw.Orchestrator.exe'; Display = 'Entire Team Workflow - Orchestrator'; Port = 5182; Text = 'Runs the multi-agent team (Microsoft Agent Framework + Claude).' },
    @{ Name = 'EtwAzure';        Folder = 'Azure';        Exe = 'Etw.Azure.exe';        Display = 'Entire Team Workflow - Azure';        Port = 5183; Text = 'Connects to Azure and deploys finished applications.' }
)

function Grant-LogonAsService([string]$account) {
    $sid = (New-Object Security.Principal.NTAccount($account)).Translate([Security.Principal.SecurityIdentifier]).Value
    $cfg = Join-Path $env:TEMP "etw-secpol-$PID.inf"
    $db = Join-Path $env:TEMP "etw-secpol-$PID.sdb"
    try {
        secedit /export /cfg $cfg /areas USER_RIGHTS | Out-Null
        $lines = [System.Collections.Generic.List[string]](Get-Content $cfg)
        $i = $lines.FindIndex({ param($l) $l -match '^SeServiceLogonRight' })
        if ($i -ge 0) {
            if ($lines[$i] -match [regex]::Escape($sid)) { return }
            $lines[$i] = $lines[$i] + ",*$sid"
        } else {
            $h = $lines.FindIndex({ param($l) $l -match '^\[Privilege Rights\]' })
            $lines.Insert($h + 1, "SeServiceLogonRight = *$sid")
        }
        Set-Content $cfg $lines -Encoding Unicode
        secedit /configure /db $db /cfg $cfg /areas USER_RIGHTS | Out-Null
    } finally {
        Remove-Item $cfg, $db -Force -ErrorAction SilentlyContinue
    }
}

# ---- 1. Programs -------------------------------------------------------------------------------------------------
if (-not (Test-Path (Join-Path $SourceDir 'Backend\Etw.Backend.exe'))) {
    Write-Host 'Publishing the solution first...' -ForegroundColor Cyan
    $publish = @{ Output = $SourceDir }
    if ($SelfContained) { $publish.SelfContained = $true }
    & (Join-Path $PSScriptRoot 'Publish-All.ps1') @publish
}

foreach ($s in $services) {
    if (Get-Service -Name $s.Name -ErrorAction SilentlyContinue) {
        Write-Host "Stopping existing $($s.Name)..."
        Stop-Service -Name $s.Name -Force -ErrorAction SilentlyContinue
        (Get-Service -Name $s.Name).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
}
Get-Process -Name 'Etw.Desktop' -ErrorAction SilentlyContinue | Stop-Process -Force

foreach ($folder in ($services | ForEach-Object { $_.Folder }) + 'Desktop') {
    $target = Join-Path $InstallDir $folder
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item -Path (Join-Path $SourceDir "$folder\*") -Destination $target -Recurse -Force
}
Write-Host "Programs copied to $InstallDir" -ForegroundColor Green

# ---- 2. Data folder and API key ----------------------------------------------------------------------------------
New-Item -ItemType Directory -Force $dataDir | Out-Null
$keyFile = Join-Path $dataDir 'api.key'
if (-not (Test-Path $keyFile)) {
    $bytes = New-Object byte[] 32
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    [IO.File]::WriteAllText($keyFile, (($bytes | ForEach-Object { $_.ToString('X2') }) -join ''))
}

$runAs = if ($LocalSystem) { 'NT AUTHORITY\SYSTEM' } else { $ServiceUser }
$desktopUser = "$env:USERDOMAIN\$env:USERNAME"

# The key lets whoever reads it drive agents that edit files: only SYSTEM, Administrators, the service account and you.
$grants = @('NT AUTHORITY\SYSTEM', 'BUILTIN\Administrators', $runAs, $desktopUser) | Select-Object -Unique
icacls $dataDir /inheritance:r | Out-Null
foreach ($g in $grants) { icacls $dataDir /grant "${g}:(OI)(CI)F" | Out-Null }

# ---- 3. Services -------------------------------------------------------------------------------------------------
$credential = $null
if (-not $LocalSystem) {
    $credential = Get-Credential -UserName $ServiceUser -Message 'Password of the account the services run as (it needs your Claude sign-in)'
    Grant-LogonAsService $credential.UserName
}

foreach ($s in $services) {
    $exe = Join-Path $InstallDir "$($s.Folder)\$($s.Exe)"
    if (Get-Service -Name $s.Name -ErrorAction SilentlyContinue) { sc.exe delete $s.Name | Out-Null; Start-Sleep -Seconds 1 }

    $params = @{ Name = $s.Name; BinaryPathName = "`"$exe`""; DisplayName = $s.Display; Description = $s.Text; StartupType = 'Automatic' }
    if ($credential) { $params.Credential = $credential }
    New-Service @params | Out-Null

    # Restart after a crash: 5 s, 5 s, then 30 s.
    sc.exe failure $s.Name reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
    Write-Host "Installed $($s.Name) (runs as $runAs)"
}

foreach ($s in $services) { Start-Service -Name $s.Name }

# ---- 4. Wait until everything answers ----------------------------------------------------------------------------
$deadline = (Get-Date).AddSeconds(60)
foreach ($s in $services) {
    $up = $false
    while ((Get-Date) -lt $deadline -and -not $up) {
        try { Invoke-RestMethod "http://127.0.0.1:$($s.Port)/health" -TimeoutSec 2 | Out-Null; $up = $true } catch { Start-Sleep -Milliseconds 500 }
    }
    if ($up) { Write-Host "  $($s.Name) is running" -ForegroundColor Green } else { Write-Warning "$($s.Name) did not answer on port $($s.Port). See $dataDir\logs." }
}

# ---- 5. Shortcut -------------------------------------------------------------------------------------------------
$shell = New-Object -ComObject WScript.Shell
foreach ($place in @([Environment]::GetFolderPath('CommonPrograms'), [Environment]::GetFolderPath('CommonDesktopDirectory'))) {
    $lnk = $shell.CreateShortcut((Join-Path $place 'Entire Team Workflow.lnk'))
    $lnk.TargetPath = Join-Path $InstallDir 'Desktop\Etw.Desktop.exe'
    $lnk.WorkingDirectory = Join-Path $InstallDir 'Desktop'
    $lnk.Save()
}

Write-Host ''
Write-Host 'Entire Team Workflow is installed. Open it from the Start menu or the desktop shortcut.' -ForegroundColor Green
if ($LocalSystem) { Write-Warning 'Services run as LocalSystem: the agents have no Claude sign-in. Reinstall without -LocalSystem to use your account.' }
