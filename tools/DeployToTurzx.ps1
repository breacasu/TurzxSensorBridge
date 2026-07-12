# DeployToTurzx.ps1
# Baut PatchModule + SensorService im Release-Modus und deployed beide
# in das TURZX-Installationsverzeichnis (patches\PatchModule.dll und
# patches\SensorService\SensorService.exe + Abhaengigkeiten).

param(
    [Parameter(Mandatory = $true, HelpMessage = "Absolute path to the TURZX installation directory")]
    [string]$TurzxDir,
    [string]$Configuration = "Release"
)

$repoRoot = Split-Path -Parent $PSScriptRoot

Write-Host "=== Build PatchModule ($Configuration) ==="
dotnet build "$repoRoot\src\PatchModule\PatchModule.csproj" -c $Configuration
if ($LASTEXITCODE -ne 0) { Write-Error "PatchModule build failed"; exit 1 }

Write-Host "=== Build SensorService ($Configuration) ==="
dotnet build "$repoRoot\src\SensorService\SensorService.csproj" -c $Configuration
if ($LASTEXITCODE -ne 0) { Write-Error "SensorService build failed"; exit 1 }

$patchesDir = Join-Path $TurzxDir "patches"
$sensorServiceDestDir = Join-Path $patchesDir "SensorService"

New-Item -ItemType Directory -Path $patchesDir -Force | Out-Null
New-Item -ItemType Directory -Path $sensorServiceDestDir -Force | Out-Null

Write-Host "=== Stopping running processes ==="
Get-Process -Name "TURZX", "TurzxPatcher", "SensorService" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

Write-Host "=== Deploying PatchModule.dll ==="
Copy-Item "$repoRoot\src\PatchModule\bin\$Configuration\net48\PatchModule.dll" -Destination (Join-Path $patchesDir "PatchModule.dll") -Force

Write-Host "=== Deploying SensorService ==="
Copy-Item "$repoRoot\src\SensorService\bin\$Configuration\net48\win-x64\*" -Destination $sensorServiceDestDir -Recurse -Force

Write-Host "Done. Deployed to: $TurzxDir"
Write-Host "Start TurzxPatcher.exe as Administrator - SensorService.exe will be auto-started."
