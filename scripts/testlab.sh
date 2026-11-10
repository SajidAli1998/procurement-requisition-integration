#!/usr/bin/env bash
# Starts the Test Lab: its API on :5110 and the web app on :3000, sharing one lab key.
# Everything runs against an in-memory, isolated lab; nothing can reach a real SAP.
# Stop with scripts/testlab-stop.sh. Logs and PIDs go to .run/.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RUN="$ROOT/.run"
mkdir -p "$RUN"
cd "$ROOT"
export TESTLAB_KEY="${TESTLAB_KEY:-$(openssl rand -hex 16)}"

echo "Building the Test Lab API..."
dotnet build src/Procurement.TestLab --nologo -v quiet -clp:ErrorsOnly
if ! curl -sf http://localhost:5110/api/health >/dev/null 2>&1; then
  nohup dotnet src/Procurement.TestLab/bin/Debug/net10.0/testlab.dll serve > "$RUN/testlab.log" 2>&1 &
  echo $! > "$RUN/testlab.pid"
fi
for _ in $(seq 1 60); do curl -sf http://localhost:5110/api/health >/dev/null 2>&1 && break; sleep 0.5; done
echo "  Test Lab API: http://localhost:5110 (OpenAPI: /scalar)"

cd web
if [[ ! -d node_modules ]]; then npm install --no-audit --no-fund; fi
printf 'TESTLAB_URL=http://localhost:5110\nTESTLAB_KEY=%s\n' "$TESTLAB_KEY" > .env.local
if ! curl -sf http://localhost:3000 >/dev/null 2>&1; then
  nohup npm run dev > "$RUN/web.log" 2>&1 &
  echo $! > "$RUN/web.pid"
fi
for _ in $(seq 1 120); do curl -sf http://localhost:3000 >/dev/null 2>&1 && break; sleep 0.5; done
echo "  Web app:      http://localhost:3000"
echo "Stop with: scripts/testlab-stop.sh"
