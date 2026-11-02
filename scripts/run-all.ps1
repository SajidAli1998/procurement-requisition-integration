# Builds the solution and starts the three services in the background:
#   source mock :5101, SAP mock :5102, assistant API :5103.
# Logs and PIDs go to .run/. Stop them with scripts/stop-all.ps1.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$run = Join-Path $root '.run'
New-Item -ItemType Directory -Force -Path $run | Out-Null
Set-Location $root

Write-Host 'Building...'
dotnet build ProcurementIntegration.slnx --nologo -v quiet -clp:ErrorsOnly
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

function Test-Health([int]$port) {
    try { Invoke-WebRequest -Uri "http://localhost:$port/health" -UseBasicParsing -TimeoutSec 2 | Out-Null; return $true }
    catch { return $false }
}

function Start-Service([string]$name, [string]$project, [int]$port) {
    if (Test-Health $port) { Write-Host "  $name already running on :$port"; return }
    $bin = Join-Path $root "src/$project/bin/Debug/net10.0"
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $process = Start-Process -FilePath 'dotnet' -PassThru -NoNewWindow `
        -ArgumentList @("$bin/$project.dll", '--urls', "http://localhost:$port", '--contentRoot', $bin) `
        -RedirectStandardOutput (Join-Path $run "$name.log") -RedirectStandardError (Join-Path $run "$name.err.log")
    Set-Content -Path (Join-Path $run "$name.pid") -Value $process.Id
}

Start-Service 'source' 'Mocks.SourceApi' 5101
Start-Service 'sap' 'Mocks.SapApi' 5102
Start-Service 'assistant' 'Procurement.AssistantApi' 5103

foreach ($port in 5101, 5102, 5103) {
    $deadline = (Get-Date).AddSeconds(30)
    while (-not (Test-Health $port)) {
        if ((Get-Date) -gt $deadline) { throw "Service on :$port did not start; see .run/*.log" }
        Start-Sleep -Milliseconds 500
    }
}

Write-Host @'
Services are up:
  Source mock     http://localhost:5101/scalar
  SAP mock        http://localhost:5102/scalar
  Assistant API   http://localhost:5103/scalar
Run a sync:       dotnet src/Procurement.Sync.Worker/bin/Debug/net10.0/procsync.dll run
Stop everything:  scripts/stop-all.ps1
'@
