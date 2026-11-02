#!/usr/bin/env bash
# Exports the OpenAPI documents of all three services into api/ and regenerates the MCP tool definitions.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
scripts/run-all.sh >/dev/null
python_pretty() { python3 -c 'import json,sys; json.dump(json.load(sys.stdin), sys.stdout, indent=2); print()'; }
curl -sf http://localhost:5103/openapi/v1.json | python_pretty > api/assistant-api.openapi.json
curl -sf http://localhost:5101/openapi/v1.json | python_pretty > api/source-mock.openapi.json
curl -sf http://localhost:5102/openapi/v1.json | python_pretty > api/sap-mock.openapi.json
python3 scripts/generate_mcp_tools.py api/assistant-api.openapi.json > api/mcp-tools.json
echo "Exported:"; ls -1 api/*.json
