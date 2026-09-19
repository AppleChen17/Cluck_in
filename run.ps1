#Requires -Version 5.1
<#
.SYNOPSIS
    Starts the AI engine and the WPF dashboard together.

.DESCRIPTION
    The AI engine goes into its own window so uvicorn's --reload output stays
    readable. The WPF app runs here in the foreground; closing it (or Ctrl+C)
    shuts the engine window down too.

.PARAMETER EngineOnly
    Start the AI engine only.

.PARAMETER AppOnly
    Start the WPF dashboard only, assuming the engine is already up.
#>
param(
    [switch]$EngineOnly,
    [switch]$AppOnly
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$engineDir = Join-Path $root 'src/ai-engine'
$venvPython = Join-Path $engineDir '.venv/Scripts/python.exe'
$appProject = Join-Path $root 'src/app/CluckIn.App.csproj'

function Test-EngineUp {
    try {
        $null = Invoke-WebRequest -Uri 'http://127.0.0.1:8000/docs' -UseBasicParsing -TimeoutSec 1
        return $true
    } catch {
        return $false
    }
}

$engine = $null

if (-not $AppOnly) {
    if (Test-Path $venvPython) {
        $python = $venvPython
    } else {
        Write-Host "[run] No venv at $venvPython - falling back to 'python' on PATH." -ForegroundColor Yellow
        Write-Host "[run] To create it: python -m venv src/ai-engine/.venv; src/ai-engine/.venv/Scripts/python.exe -m pip install -r src/ai-engine/requirements.txt" -ForegroundColor Yellow
        $python = 'python'
    }

    if (Test-EngineUp) {
        Write-Host '[run] AI engine already listening on :8000 - reusing it.' -ForegroundColor Green
    } else {
        Write-Host '[run] Starting AI engine (:8000)...' -ForegroundColor Cyan
        # start.py runs `uvicorn app:app` without --app-dir, so the working
        # directory has to be src/ai-engine or the module is not found.
        $engine = Start-Process -FilePath 'powershell.exe' `
            -ArgumentList '-NoExit', '-NoProfile', '-Command', "& '$python' start.py" `
            -WorkingDirectory $engineDir -PassThru

        for ($i = 0; $i -lt 60; $i++) {
            if (Test-EngineUp) { break }
            Start-Sleep -Milliseconds 500
        }

        if (Test-EngineUp) {
            Write-Host '[run] AI engine ready: http://127.0.0.1:8000/docs' -ForegroundColor Green
        } else {
            Write-Host '[run] AI engine did not answer within 30s - check its window (Ollama model pull can be slow).' -ForegroundColor Yellow
        }
    }
}

if ($EngineOnly) {
    Write-Host '[run] Engine started. Its window stays open; close it to stop.' -ForegroundColor Cyan
    return
}

try {
    Write-Host '[run] Starting WPF dashboard...' -ForegroundColor Cyan
    dotnet run --project $appProject
} finally {
    if ($engine -and -not $engine.HasExited) {
        Write-Host '[run] Stopping AI engine...' -ForegroundColor Cyan
        # /T so uvicorn and the ollama serve child go down with the window.
        taskkill /PID $engine.Id /T /F 2>&1 | Out-Null
    }
}
