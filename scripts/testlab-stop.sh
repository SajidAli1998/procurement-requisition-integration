#!/usr/bin/env bash
# Stops what scripts/testlab.sh started.
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
for name in web testlab; do
  pid_file="$ROOT/.run/$name.pid"
  if [[ -f "$pid_file" ]]; then
    kill "$(cat "$pid_file")" 2>/dev/null || true
    pkill -P "$(cat "$pid_file")" 2>/dev/null || true
    rm -f "$pid_file"
    echo "stopped $name"
  fi
done
