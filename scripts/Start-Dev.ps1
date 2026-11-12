<#
.SYNOPSIS
  Builds the solution and starts the four services as ordinary console processes (no Windows service install needed).

.PARAMETER Hidden
  Run the services without console windows; output goes to <DataDir>\logs\dev-<service>.out.

.PARAMETER DataDir
  Use a different data folder (database, secrets, API key). Handy for trying things without touching the installed services.

.PARAMETER Stop
  Stop the dev services started by this script.

.PARAMETER Desktop
  Also start the desktop app.
#>
param(
    [switch]$Hidden,
    [string]$DataDir,
    [switch]$Stop,
    [switch]$Desktop,
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$services = @(
    @{ Name = 'Etw.Backend';       Port = 5180 },
    @{ Name = 'Etw.Integrations';  Port = 5181 },
    @{ Name = 'Etw.Orchestrator';  Port = 5182 },
    @{ Name = 'Etw.Azure';         Port = 5183 }
)

function Stop-Dev {
    foreach ($s in $services) {
        Get-Process -Name $s.Name -ErrorAction SilentlyContinue | Stop-Process -Force
    }
}

if ($Stop) { Stop-Dev; Write-Host 'Stopped.'; return }

Stop-Dev
Write-Host "Building ($Configuration)..." -ForegroundColor Cyan
dotnet build "$root\EntireTeamWorkflow.slnx" -c $Configuration --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

if ($DataDir) { $env:ETW_DATA_DIR = $DataDir; New-Item -ItemType Directory -Force $DataDir | Out-Null }
$logDir = if ($DataDir) { Join-Path $DataDir 'logs' } else { Join-Path $env:ProgramData 'EntireTeamWorkflow\logs' }
New-Item -ItemType Directory -Force $logDir | Out-Null

foreach ($s in $services) {
    $exe = Join-Path $root "src\$($s.Name)\bin\$Configuration\net10.0\$($s.Name).exe"
    if (-not (Test-Path $exe)) { throw "Missing $exe" }
    if ($Hidden) {
        Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -WindowStyle Hidden `
            -RedirectStandardOutput (Join-Path $logDir "dev-$($s.Name).out") -RedirectStandardError (Join-Path $logDir "dev-$($s.Name).err")
    } else {
        Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe)
    }
    Write-Host "Started $($s.Name) on http://127.0.0.1:$($s.Port)"
}

# Wait until every service answers /health.
$deadline = (Get-Date).AddSeconds(40)
foreach ($s in $services) {
    while ((Get-Date) -lt $deadline) {
        try { Invoke-RestMethod "http://127.0.0.1:$($s.Port)/health" -TimeoutSec 2 | Out-Null; break } catch { Start-Sleep -Milliseconds 400 }
    }
}
Write-Host 'All services are up.' -ForegroundColor Green

if ($Desktop) {
    $app = (Get-ChildItem (Join-Path $root "src\Etw.Desktop\bin\$Configuration") -Filter Etw.Desktop.exe -Recurse | Select-Object -First 1).FullName
    Start-Process -FilePath $app -WorkingDirectory (Split-Path $app)
}

