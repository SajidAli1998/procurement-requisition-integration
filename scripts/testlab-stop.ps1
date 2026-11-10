#Requires -Version 7.0
# Stops what scripts/testlab.ps1 started.
$root = Split-Path -Parent $PSScriptRoot
foreach ($name in 'web', 'testlab') {
    $pidFile = Join-Path $root ".run/$name.pid"
    if (Test-Path $pidFile) {
        Stop-Process -Id (Get-Content $pidFile) -Force -ErrorAction SilentlyContinue
        Remove-Item $pidFile
        Write-Host "stopped $name"
    }
}
