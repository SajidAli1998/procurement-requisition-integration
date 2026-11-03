#!/usr/bin/env bash
# Guided demo of the whole solution on the client's synthetic data workbook. Starts the services if needed.
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

# The workbook's 429s ask for 20 seconds; more parallel sends keep the demo short while still honouring every wait.
# The client-side rate limit stays well under the SAP mock's own limit, so the only 429s are the workbook's.
export Sync__Parallelism=8 Sync__Resilience__PermitsPerSecond=8 Sync__RetryPendingBaseDelay=00:00:02 Sync__InDoubtRecheckDelay=00:00:02 Logging__LogLevel__Procurement=Warning
procsync() { dotnet src/Procurement.Sync.Worker/bin/Debug/net10.0/procsync.dll "$@" || true; }
step() {
  echo
  echo "════════════════════════════════════════════════════════════════════"
  echo "  $1"
  echo "════════════════════════════════════════════════════════════════════"
  if $PAUSE; then read -r -p "  (press Enter) "; fi
}
token() { curl -s -X POST "$ASSISTANT/dev/token" "${JSON[@]}" -d "{\"user\":\"$1\"}" | grep -o '"access_token":"[^"]*"' | cut -d'"' -f4; }
outcomes() { grep -E "^($(echo "$@" | tr ' ' '|'))," reports/latest.csv | cut -d, -f1,3,4,5,6,7 | sed 's/^/  /;s/,/  |  /g'; }

scripts/run-all.sh >/dev/null

step "1. Reset: the client's workbook in the source, SAP as the workbook describes it, no local state"
curl -s -X POST "$SRC/_admin/reset?dataset=Workbook" >/dev/null
curl -s -X POST "$SAP/_admin/reset?dataset=Workbook" >/dev/null
curl -s -X DELETE "$SRC/_admin/faults" >/dev/null
curl -s -X DELETE "$SAP/_admin/faults" >/dev/null
rm -f procsync.db procsync.db-shm procsync.db-wal
echo "  120 requisitions (115 approved, 5 pending), 50 of them defective."
echo "  SAP already holds 7 of them, and will answer 28 creates with 429, 503 or 409 (Failure_Injection sheet)."

step "2. First sync run: three pages of 40, the workbook's failures, Retry-After honoured (about a minute)"
procsync run

step "3. Per requisition: what happened and why (also in reports/latest.csv)"
outcomes REQ-7001 REQ-7002 REQ-7003 REQ-7004 REQ-7005 REQ-7066 REQ-7071 REQ-7072
echo
procsync check-expected

step "4. Run again: nothing is created twice"
procsync run
echo
echo "  SAP's own view (duplicates by external reference):"
curl -s "$SAP/_admin/stats" | grep -o '"total":[0-9]*,\|"duplicateExternalReferences":\[[^]]*\]'

step "5. What is in quarantine, and why?"
procsync quarantine list --reason AmountNotPositive | sed -n '1,4p;$p'
echo
procsync quarantine list --reason MissingRequiredField | sed -n '2,4p;$p'

step "6. Someone fixes two quarantined requisitions in the source; the next run releases them"
curl -s -X POST "$SRC/_admin/requisitions/REQ-7071/edit" "${JSON[@]}" -d '{"totalAmount":25452}' >/dev/null
curl -s -X POST "$SRC/_admin/requisitions/REQ-7072/edit" "${JSON[@]}" -d '{"neededBy":"2027-02-10"}' >/dev/null
echo "  REQ-7071: total 0 -> 25,452 (the sum of its lines);  REQ-7072: required-by date added"
procsync run
echo
outcomes REQ-7071 REQ-7072

step "7. Purchasing works the requisitions in SAP; statuses flow back to the source"
for _ in 1 2 3; do curl -s -X POST "$SAP/_admin/advance" >/dev/null; done
procsync run >/dev/null
procsync status REQ-7005

step "8. The AI assistant, signed in as different employees"
NEEDED=$(date -u -v+30d +%F 2>/dev/null || date -u -d '+30 days' +%F)
E1003=(-H "Authorization: Bearer $(token E1003)")
E1001=(-H "Authorization: Bearer $(token E1001)")
E1002=(-H "Authorization: Bearer $(token E1002)")
E1008=(-H "Authorization: Bearer $(token E1008)")
echo "  E1003 asks about their own REQ-7003:"
curl -s "${E1003[@]}" "$ASSISTANT/v1/requisition-requests/REQ-7003" | grep -o '"status":"[^"]*","statusReason":"[^"]*"' | sed 's/^/    /'
echo "  E1001 asks about REQ-7001, raised by a colleague:"
curl -s "${E1001[@]}" -w ' (HTTP %{http_code})' "$ASSISTANT/v1/requisition-requests/REQ-7001" | grep -o '"code":"[^"]*".*' | sed 's/^/    /'
echo; echo "  E1002 validates a request with mistakes:"
curl -s "${E1002[@]}" "${JSON[@]}" -X POST "$ASSISTANT/v1/requisition-requests:validate" \
  -d "{\"costCenter\":\"CC-99\",\"plant\":\"1100\",\"neededBy\":\"$NEEDED\",\"justification\":\"Two route control displays for the depot\",\"lines\":[{\"materialCode\":\"SCREEN\",\"quantity\":2}]}" | sed 's/^/    /'
BODY="{\"costCenter\":\"CC-02\",\"plant\":\"3100\",\"neededBy\":\"$NEEDED\",\"justification\":\"Two route control displays for the depot\",\"lines\":[{\"materialCode\":\"TEST-002-01\",\"quantity\":2}],\"assistantSessionRef\":\"demo\"}"
echo; echo; echo "  ...corrects it; validate returns a summary to show the user, and a confirmation token:"
VALID=$(curl -s "${E1002[@]}" "${JSON[@]}" -X POST "$ASSISTANT/v1/requisition-requests:validate" -d "$BODY")
echo "$VALID" | grep -o '"confirmationSummary":"[^"]*"' | sed 's/^/    /'
CONFIRMATION=$(echo "$VALID" | grep -o '"confirmationToken":"[^"]*"' | cut -d'"' -f4)
echo "  The user says yes; the assistant creates it with the token:"
CREATED=$(curl -s "${E1002[@]}" "${JSON[@]}" -H "Idempotency-Key: demo-$(date +%s)" -H "X-User-Confirmation: $CONFIRMATION" \
  -X POST "$ASSISTANT/v1/requisition-requests" -d "$BODY")
echo "$CREATED" | grep -o '"id":"[^"]*","status":"[^"]*"' | sed 's/^/    /'
ID=$(echo "$CREATED" | grep -o '"id":"[^"]*"' | head -1 | cut -d'"' -f4)
echo "  E1008 may read but not raise requisitions:"
curl -s "${E1008[@]}" "${JSON[@]}" -w ' (HTTP %{http_code})' -X POST "$ASSISTANT/v1/requisition-requests" -H "Idempotency-Key: demo-e1008" -d "$BODY" \
  | grep -o '"code":"[^"]*".*' | sed 's/^/    /'
echo

step "9. A person approves it; the integration transfers it; the assistant reads the status"
curl -s -X POST "$SRC/_admin/requisitions/$ID/edit" "${JSON[@]}" \
  -d "{\"status\":\"Approved\",\"approvedAt\":\"$(date -u +%FT%TZ)\"}" >/dev/null
echo "  $ID approved."
procsync run >/dev/null
curl -s "${E1002[@]}" "$ASSISTANT/v1/requisition-requests/$ID" | grep -o '"status":"[^"]*","statusReason":"[^"]*"' | sed 's/^/    /'

step "10. Reconciliation: prove nothing was lost or duplicated"
procsync reconcile
echo
echo "  Open the HTML report: $ROOT/reports/latest.html"
echo "  Stop the services with: scripts/stop-all.sh"
