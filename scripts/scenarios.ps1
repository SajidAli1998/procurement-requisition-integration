#Requires -Version 7.0
# The scenario runner in the terminal (see scripts/scenarios.sh for the commands).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$dll = 'src/Procurement.TestLab/bin/Debug/net10.0/testlab.dll'
if (-not (Test-Path $dll) -or $env:REBUILD -eq '1') { dotnet build src/Procurement.TestLab --nologo -v quiet -clp:ErrorsOnly }
dotnet $dll @args
exit $LASTEXITCODE
