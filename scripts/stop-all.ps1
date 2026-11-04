#Requires -Version 7.0
# Stops the services started by scripts/run-all.ps1.
$run = Join-Path (Split-Path -Parent $PSScriptRoot) '.run'
Get-ChildItem -Path $run -Filter '*.pid' -ErrorAction SilentlyContinue | ForEach-Object {
    $processId = [int](Get-Content $_.FullName)
    Stop-Process -Id $processId -ErrorAction SilentlyContinue
    Write-Host "Stopped $($_.BaseName)"
    Remove-Item $_.FullName
}
