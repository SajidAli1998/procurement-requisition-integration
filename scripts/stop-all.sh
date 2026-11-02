#!/usr/bin/env bash
# Stops the services started by scripts/run-all.sh.
set -uo pipefail
RUN="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/.run"
for pidfile in "$RUN"/*.pid; do
  [ -e "$pidfile" ] || continue
  kill "$(cat "$pidfile")" 2>/dev/null && echo "Stopped $(basename "$pidfile" .pid)"
  rm -f "$pidfile"
done
