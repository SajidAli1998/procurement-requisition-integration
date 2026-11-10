#Requires -Version 7.0
# Starts the Test Lab API on :5110 and the web app on :3000 with a shared key. Stop with scripts/testlab-stop.ps1.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$run = Join-Path $root '.run'
New-Item -ItemType Directory -Force $run | Out-Null
Set-Location $root
if (-not $env:TESTLAB_KEY) { $env:TESTLAB_KEY = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(16)) }

function Wait-Url($url) {
    foreach ($i in 1..120) {
        try { Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 2 | Out-Null; return } catch { Start-Sleep -Milliseconds 500 }
    }
}

Write-Host 'Building the Test Lab API...'
dotnet build src/Procurement.TestLab --nologo -v quiet -clp:ErrorsOnly
$api = Start-Process dotnet -ArgumentList 'src/Procurement.TestLab/bin/Debug/net10.0/testlab.dll', 'serve' -PassThru -NoNewWindow `
    -RedirectStandardOutput (Join-Path $run 'testlab.log') -RedirectStandardError (Join-Path $run 'testlab.err')
$api.Id | Set-Content (Join-Path $run 'testlab.pid')
Wait-Url 'http://localhost:5110/api/health'
Write-Host '  Test Lab API: http://localhost:5110'

Set-Location (Join-Path $root 'web')
if (-not (Test-Path node_modules)) { npm install --no-audit --no-fund }
"TESTLAB_URL=http://localhost:5110`nTESTLAB_KEY=$($env:TESTLAB_KEY)" | Set-Content .env.local
$web = Start-Process npm -ArgumentList 'run', 'dev' -PassThru -NoNewWindow `
    -RedirectStandardOutput (Join-Path $run 'web.log') -RedirectStandardError (Join-Path $run 'web.err')
$web.Id | Set-Content (Join-Path $run 'web.pid')
Wait-Url 'http://localhost:3000'
Write-Host '  Web app:      http://localhost:3000'
Write-Host 'Stop with: pwsh scripts/testlab-stop.ps1'
