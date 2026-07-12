# TurzxPatcherTest.ps1
# Startet TurzxPatcher, wartet N Sekunden, schliesst alles automatisch.
# SensorService wird automatisch von PatchModule.dll gestartet.

param(
    [Parameter(Mandatory = $true, HelpMessage = "Absolute path to the TURZX installation directory (where TurzxPatcher.exe lives)")]
    [string]$TurzxDir,
    [int]$RuntimeSec = 45,
    [string]$OutputFile = "$env:TEMP\tp_test.txt"
)

$tpExe = Join-Path $TurzxDir "TurzxPatcher.exe"

Write-Host "=== TurzxPatcher Test Runner ==="
Write-Host "TurzxDir: $TurzxDir"
Write-Host "Runtime:  $RuntimeSec seconds"
Write-Host "Output:   $OutputFile"

if (-not (Test-Path $tpExe)) {
    Write-Error "TurzxPatcher.exe not found at: $tpExe"
    exit 1
}

# Alte Prozesse killen (mit TIMEOUT!)
Write-Host "Killing old processes..."
$procs = Get-Process -Name "TURZX","TurzxPatcher","SensorService" -ErrorAction SilentlyContinue
if ($procs) {
    $procs | Stop-Process -Force -ErrorAction SilentlyContinue
    # Warte bis sie WIRKLICH weg sind
    $waitStart = Get-Date
    while (((Get-Date) - $waitStart).TotalSeconds -lt 5) {
        $stillRunning = Get-Process -Name "TURZX","TurzxPatcher","SensorService" -ErrorAction SilentlyContinue
        if (-not $stillRunning) { break }
        Start-Sleep -Milliseconds 200
    }
    Write-Host "  All killed"
}
Start-Sleep 1

# Backup Kill-Logik: killt nach RuntimeSec+10s egal was passiert
$killJob = Start-Job -ScriptBlock {
    param($sec, $outFile)
    Start-Sleep -Seconds ($sec + 10)
    $procs = Get-Process -Name "TURZX","TurzxPatcher","SensorService" -ErrorAction SilentlyContinue
    if ($procs) {
        $procs | Stop-Process -Force -ErrorAction SilentlyContinue
        Add-Content -Path $outFile -Value "[AUTO-KILL] Killed after $($sec + 10)s timeout"
    }
} -ArgumentList $RuntimeSec, $OutputFile

# SensorService wird jetzt automatisch von PatchModule.dll gestartet
# (patches\SensorService\SensorService.exe relativ zum Plugin-Pfad) -
# kein manueller Start mehr noetig.

# TurzxPatcher starten
Write-Host "Starting TurzxPatcher..."
$tp = Start-Process -FilePath $tpExe -RedirectStandardOutput $OutputFile -PassThru
Write-Host "  Launcher PID: $($tp.Id) (this process copies itself and starts a --worker child, then exits)"

# The launcher process starts a NEW "--worker" child process and exits itself
# almost immediately. Wait for the actual worker process (which hosts TURZX)
# to appear so we track the right PID for the runtime duration.
$workerProc = $null
$waitStart = Get-Date
while (((Get-Date) - $waitStart).TotalSeconds -lt 15) {
    $workerProc = Get-Process -Name "TurzxPatcher" -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $tp.Id } | Select-Object -First 1
    if ($workerProc) { break }
    Start-Sleep -Milliseconds 300
}
if ($workerProc) {
    Write-Host "  Worker PID: $($workerProc.Id)"
} else {
    Write-Host "  WARNING: worker process not found after 15s, falling back to launcher PID"
    $workerProc = $tp
}

Write-Host "Sleeping $RuntimeSec seconds (will be auto-killed after $RuntimeSec+10s)..."
for ($i = 1; $i -le $RuntimeSec; $i++) {
    Start-Sleep -Seconds 1
    if ((Get-Process -Id $workerProc.Id -ErrorAction SilentlyContinue) -eq $null) {
        Write-Host "  TurzxPatcher worker exited after $i seconds"
        break
    }
    if ($i % 10 -eq 0) { Write-Host "  ... $i / $RuntimeSec" }
}

# Cleanup
Stop-Job $killJob -ErrorAction SilentlyContinue
Remove-Job $killJob -ErrorAction SilentlyContinue
Get-Process -Name "TURZX","TurzxPatcher","SensorService" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep 1

Write-Host "Done. Output: $OutputFile"
