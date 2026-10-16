# Starts FireVision AI: the API, the dashboard and the AI service, each in its own window.
# Usage (PowerShell, in the project folder):   .\start.ps1            add -Simulator for the fire demo
# If scripts are blocked:                      powershell -ExecutionPolicy Bypass -File .\start.ps1
param([switch]$Simulator)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$envFile = Join-Path $root '.env'
if (-not (Test-Path $envFile)) { Write-Host "No .env file. Copy .env.example to .env and fill it in first (see the setup guide)." -ForegroundColor Red; exit 1 }
# The AI service and the simulator need the same device key as the API.
$key = (Get-Content $envFile | Where-Object { $_ -match '^\s*Device__ApiKey=' } | Select-Object -First 1) -replace '^\s*Device__ApiKey=', ''
if (-not $key) { Write-Host "Device__ApiKey is empty in .env: fill it in first." -ForegroundColor Red; exit 1 }

function Open-Window([string]$title, [string]$folder, [string]$command) {
    $script = "`$host.UI.RawUI.WindowTitle = '$title'; Set-Location -LiteralPath '$folder'; $command"
    Start-Process powershell -ArgumentList '-NoExit', '-ExecutionPolicy', 'Bypass', '-Command', $script | Out-Null
    Write-Host "Started: $title"
}

Open-Window 'FireVision AI - API' (Join-Path $root 'backend') 'dotnet run --project Sentinel.Api --launch-profile http'
Open-Window 'FireVision AI - Dashboard' (Join-Path $root 'frontend') 'ng serve'
$python = Join-Path $root 'ai-service\.venv\Scripts\python.exe'
if (Test-Path $python) {
    Open-Window 'FireVision AI - AI service' (Join-Path $root 'ai-service') "`$env:SENTINEL_DEVICE_KEY = '$key'; .\.venv\Scripts\python -m sentinel_ai"
} else {
    Write-Host "AI service skipped: ai-service\.venv is missing (see the setup guide, Step 4)." -ForegroundColor Yellow
}
if ($Simulator) {
    Open-Window 'FireVision AI - Simulator' (Join-Path $root 'backend') "Start-Sleep 20; dotnet run --project Sentinel.Simulator -- --device-key '$key' --ignite 'Ground Floor - Warehouse'"
}
Write-Host "`nOpen http://localhost:4200 in about 30 seconds. Close a window (or press Ctrl+C in it) to stop that part." -ForegroundColor Green
