#!/usr/bin/env bash
# The scenario runner in the terminal. Each scenario runs in its own isolated, in-memory lab.
#   scripts/scenarios.sh list                     the catalogue (--category, --mode, --priority, --search)
#   scripts/scenarios.sh run DUP-012              one scenario, with its live timeline
#   scripts/scenarios.sh run 'DUP-*' 'SAP-0*'     by id prefix
#   scripts/scenarios.sh run --mode Recovery      one execution mode
#   scripts/scenarios.sh run --all                the full regression
#   scripts/scenarios.sh show DUP-012             definition and latest result
#   scripts/scenarios.sh history                  earlier runs
# Results: reports/scenarios/latest.json and reports/scenarios/batches/. Exit code 0 when nothing failed.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
DLL="src/Procurement.TestLab/bin/Debug/net10.0/testlab.dll"
if [[ ! -f "$DLL" || "${REBUILD:-0}" == "1" ]]; then
  dotnet build src/Procurement.TestLab --nologo -v quiet -clp:ErrorsOnly
fi
exec dotnet "$DLL" "$@"
