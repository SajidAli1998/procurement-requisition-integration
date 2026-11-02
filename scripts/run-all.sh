#!/usr/bin/env bash
# Builds the solution and starts the three services in the background:
#   source mock :5101, SAP mock :5102, assistant API :5103.
# Logs and PIDs go to .run/. Stop them with scripts/stop-all.sh.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RUN="$ROOT/.run"
mkdir -p "$RUN"
cd "$ROOT"

echo "Building..."
dotnet build ProcurementIntegration.slnx --nologo -v quiet -clp:ErrorsOnly

start() {
  local name=$1 project=$2 port=$3
  if curl -sf "http://localhost:$port/health" >/dev/null 2>&1; then
    echo "  $name already running on :$port"
    return
  fi
  local bin="$ROOT/src/$project/bin/Debug/net10.0"
  ASPNETCORE_ENVIRONMENT=Development nohup dotnet "$bin/$project.dll" \
    --urls "http://localhost:$port" --contentRoot "$bin" > "$RUN/$name.log" 2>&1 &
  echo $! > "$RUN/$name.pid"
}

start source Mocks.SourceApi 5101
start sap Mocks.SapApi 5102
start assistant Procurement.AssistantApi 5103

for port in 5101 5102 5103; do
  for _ in $(seq 1 60); do
    curl -sf "http://localhost:$port/health" >/dev/null 2>&1 && break
    sleep 0.5
  done
  curl -sf "http://localhost:$port/health" >/dev/null 2>&1 || { echo "Service on :$port did not start; see .run/*.log"; exit 1; }
done

cat <<INFO
Services are up:
  Source mock     http://localhost:5101/scalar
  SAP mock        http://localhost:5102/scalar
  Assistant API   http://localhost:5103/scalar
Run a sync:       dotnet src/Procurement.Sync.Worker/bin/Debug/net10.0/procsync.dll run
Stop everything:  scripts/stop-all.sh
INFO
