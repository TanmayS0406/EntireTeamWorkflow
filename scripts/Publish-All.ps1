<#
.SYNOPSIS
  Publishes the four services and the desktop app into one folder, ready for Install-Services.ps1.

.PARAMETER Output
  Target folder. Default: <repo>\dist

.PARAMETER SelfContained
  Bundle the .NET runtime so the target machine needs no .NET installed (bigger output).
#>
param(
    [string]$Output,
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Output) { $Output = Join-Path $root 'dist' }

$projects = @(
    @{ Name = 'Backend';      Path = 'src\Etw.Backend\Etw.Backend.csproj' },
    @{ Name = 'Integrations'; Path = 'src\Etw.Integrations\Etw.Integrations.csproj' },
    @{ Name = 'Orchestrator'; Path = 'src\Etw.Orchestrator\Etw.Orchestrator.csproj' },
    @{ Name = 'Azure';        Path = 'src\Etw.Azure\Etw.Azure.csproj' },
    @{ Name = 'Desktop';      Path = 'src\Etw.Desktop\Etw.Desktop.csproj' }
)

foreach ($p in $projects) {
    $target = Join-Path $Output $p.Name
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }

    Write-Host "Publishing $($p.Name)..." -ForegroundColor Cyan
    $args = @('publish', (Join-Path $root $p.Path), '-c', 'Release', '-o', $target, '--nologo', '-v', 'q')
    if ($SelfContained) { $args += @('-r', 'win-x64', '--self-contained', 'true') }
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "Publishing $($p.Name) failed." }
}

Write-Host "Done: $Output" -ForegroundColor Green
