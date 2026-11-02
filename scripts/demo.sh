#!/usr/bin/env bash
# Guided demo of the whole solution. Starts the services if needed.
#   scripts/demo.sh            pauses between steps
#   scripts/demo.sh --no-pause runs straight through
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
PAUSE=true; [[ "${1:-}" == "--no-pause" ]] && PAUSE=false

SRC=http://localhost:5101
SAP=http://localhost:5102
ASSISTANT=http://localhost:5103
JSON=(-H 'content-type: application/json')

# Short retry delays so the demo does not wait 30s for RetryPending records.
export Sync__RetryPendingBaseDelay=00:00:02 Sync__InDoubtRecheckDelay=00:00:02 Logging__LogLevel__Procurement=Warning
procsync() { dotnet src/Procurement.Sync.Worker/bin/Debug/net10.0/procsync.dll "$@" || true; }
step() {
  echo
  echo "════════════════════════════════════════════════════════════════════"
  echo "  $1"
  echo "════════════════════════════════════════════════════════════════════"
  if $PAUSE; then read -r -p "  (press Enter) "; fi
}

scripts/run-all.sh >/dev/null

step "1. Reset: fresh synthetic data, empty SAP, no local state"
curl -s -X POST "$SRC/_admin/reset" >/dev/null
curl -s -X POST "$SAP/_admin/reset" >/dev/null
curl -s -X DELETE "$SRC/_admin/faults" >/dev/null
curl -s -X DELETE "$SAP/_admin/faults" >/dev/null
rm -f procsync.db procsync.db-shm procsync.db-wal
echo "  250 requisitions in the source (207 approved, 23 deliberately defective)."

step "2. Turn on chaos: SAP 10% 503s, 5% 429s, 3% lost responses; source pages overlap"
curl -s -X PUT "$SAP/_admin/faults" "${JSON[@]}" \
  -d '{"TransientErrorRate":0.10,"ThrottleRate":0.05,"LostResponseRate":0.03,"Seed":7}' >/dev/null
curl -s -X PUT "$SRC/_admin/faults" "${JSON[@]}" -d '{"pageOverlap":true}' >/dev/null
echo "  Faults are on."

step "3. First sync run under chaos"
procsync run

step "4. Run again: retries finish, nothing is duplicated"
sleep 3
procsync run
echo
echo "  SAP's own view (duplicates by external reference):"
curl -s "$SAP/_admin/stats" | grep -o '"total":[0-9]*,\|"duplicateExternalReferences":\[[^]]*\]'

step "5. Chaos off. What is in quarantine, and why?"
curl -s -X DELETE "$SRC/_admin/faults" >/dev/null
curl -s -X DELETE "$SAP/_admin/faults" >/dev/null
procsync quarantine list

step "6. Someone fixes a quarantined requisition in the source; the next run releases it"
curl -s -X POST "$SRC/_admin/requisitions/REQ-2026-000045/edit" "${JSON[@]}" -d '{"currency":"USD"}' >/dev/null
echo "  REQ-2026-000045: currency XYZ -> USD"
procsync run

step "7. Purchasing works the requisitions in SAP; statuses flow back to the source"
for _ in 1 2 3; do curl -s -X POST "$SAP/_admin/advance" >/dev/null; done
procsync run
echo
procsync status REQ-2026-000002

step "8. The AI assistant raises a request (validate first, then create)"
TOKEN=$(curl -s -X POST "$ASSISTANT/dev/token" "${JSON[@]}" -d '{"user":"demo.user@example.test"}' | grep -o '"access_token":"[^"]*"' | cut -d'"' -f4)
AUTH=(-H "Authorization: Bearer $TOKEN")
NEEDED=$(date -u -v+30d +%F 2>/dev/null || date -u -d '+30 days' +%F)
echo "  validate with mistakes:"
curl -s "${AUTH[@]}" "${JSON[@]}" -X POST "$ASSISTANT/v1/requisition-requests:validate" \
  -d "{\"costCenter\":\"CC-1000-IT\",\"plant\":\"2100\",\"neededBy\":\"$NEEDED\",\"justification\":\"Laptops for new starters\",\"lines\":[{\"materialCode\":\"LAPTOP\",\"quantity\":2}]}"
echo; echo; echo "  create (corrected):"
CREATED=$(curl -s "${AUTH[@]}" "${JSON[@]}" -H "Idempotency-Key: demo-$(date +%s)" -X POST "$ASSISTANT/v1/requisition-requests" \
  -d "{\"costCenter\":\"CC-1000-IT\",\"plant\":\"1100\",\"neededBy\":\"$NEEDED\",\"justification\":\"Laptops for two new starters in IT\",\"lines\":[{\"materialCode\":\"MAT-100001\",\"quantity\":2}],\"assistantSessionRef\":\"demo\"}")
echo "$CREATED"
ID=$(echo "$CREATED" | grep -o '"id":"[^"]*"' | head -1 | cut -d'"' -f4)

step "9. A person approves it; the integration transfers it; the assistant reads the status"
curl -s -X POST "$SRC/_admin/requisitions/$ID/edit" "${JSON[@]}" \
  -d "{\"status\":\"Approved\",\"approvedAt\":\"$(date -u +%FT%TZ)\"}" >/dev/null
echo "  $ID approved."
procsync run >/dev/null
curl -s "${AUTH[@]}" "$ASSISTANT/v1/requisition-requests/$ID"
echo

step "10. Reconciliation: prove nothing was lost or duplicated"
procsync reconcile
echo
echo "  Open the HTML report: $ROOT/reports/latest.html"
echo "  Stop the services with: scripts/stop-all.sh"
