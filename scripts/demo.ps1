# Guided demo of the whole solution (PowerShell 7+). Starts the services if needed.
#   scripts/demo.ps1            pauses between steps
#   scripts/demo.ps1 -NoPause   runs straight through
param([switch]$NoPause)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$src = 'http://localhost:5101'
$sap = 'http://localhost:5102'
$assistant = 'http://localhost:5103'

# Short retry delays so the demo does not wait 30s for RetryPending records.
$env:Sync__RetryPendingBaseDelay = '00:00:02'
$env:Sync__InDoubtRecheckDelay = '00:00:02'
$env:Logging__LogLevel__Procurement = 'Warning'

function procsync { dotnet src/Procurement.Sync.Worker/bin/Debug/net10.0/procsync.dll @args }
function Step([string]$title) {
    Write-Host ''
    Write-Host ('═' * 68)
    Write-Host "  $title"
    Write-Host ('═' * 68)
    if (-not $NoPause) { Read-Host '  (press Enter)' | Out-Null }
}
function Post([string]$url, $body = $null, [hashtable]$headers = @{}) {
    $json = if ($null -ne $body) { $body | ConvertTo-Json -Depth 10 } else { $null }
    Invoke-RestMethod -Method Post -Uri $url -Body $json -ContentType 'application/json' -Headers $headers
}

& "$PSScriptRoot/run-all.ps1" | Out-Null

Step '1. Reset: fresh synthetic data, empty SAP, no local state'
Post "$src/_admin/reset" | Out-Null
Post "$sap/_admin/reset" | Out-Null
Invoke-RestMethod -Method Delete -Uri "$src/_admin/faults" | Out-Null
Invoke-RestMethod -Method Delete -Uri "$sap/_admin/faults" | Out-Null
Remove-Item procsync.db, procsync.db-shm, procsync.db-wal -ErrorAction SilentlyContinue
Write-Host '  250 requisitions in the source (207 approved, 23 deliberately defective).'

Step '2. Turn on chaos: SAP 10% 503s, 5% 429s, 3% lost responses; source pages overlap'
Invoke-RestMethod -Method Put -Uri "$sap/_admin/faults" -ContentType 'application/json' `
    -Body '{"TransientErrorRate":0.10,"ThrottleRate":0.05,"LostResponseRate":0.03,"Seed":7}' | Out-Null
Invoke-RestMethod -Method Put -Uri "$src/_admin/faults" -ContentType 'application/json' -Body '{"pageOverlap":true}' | Out-Null
Write-Host '  Faults are on.'

Step '3. First sync run under chaos'
procsync run

Step '4. Run again: retries finish, nothing is duplicated'
Start-Sleep -Seconds 3
procsync run
$stats = Invoke-RestMethod "$sap/_admin/stats"
Write-Host ''
Write-Host "  SAP holds $($stats.total) requisitions; duplicates by external reference: $(@($stats.duplicateExternalReferences).Count)"

Step '5. Chaos off. What is in quarantine, and why?'
Invoke-RestMethod -Method Delete -Uri "$src/_admin/faults" | Out-Null
Invoke-RestMethod -Method Delete -Uri "$sap/_admin/faults" | Out-Null
procsync quarantine list

Step '6. Someone fixes a quarantined requisition in the source; the next run releases it'
Post "$src/_admin/requisitions/REQ-2026-000045/edit" @{ currency = 'USD' } | Out-Null
Write-Host '  REQ-2026-000045: currency XYZ -> USD'
procsync run

Step '7. Purchasing works the requisitions in SAP; statuses flow back to the source'
1..3 | ForEach-Object { Post "$sap/_admin/advance" | Out-Null }
procsync run
Write-Host ''
procsync status REQ-2026-000002

Step '8. The AI assistant raises a request (validate first, then create)'
$token = (Post "$assistant/dev/token" @{ user = 'demo.user@example.test' }).access_token
$auth = @{ Authorization = "Bearer $token" }
$needed = (Get-Date).ToUniversalTime().AddDays(30).ToString('yyyy-MM-dd')
Write-Host '  validate with mistakes:'
Post "$assistant/v1/requisition-requests:validate" @{
    costCenter = 'CC-1000-IT'; plant = '2100'; neededBy = $needed; justification = 'Laptops for new starters'
    lines = @(@{ materialCode = 'LAPTOP'; quantity = 2 })
} $auth | ConvertTo-Json -Depth 10
Write-Host '  create (corrected):'
$created = Post "$assistant/v1/requisition-requests" @{
    costCenter = 'CC-1000-IT'; plant = '1100'; neededBy = $needed; justification = 'Laptops for two new starters in IT'
    lines = @(@{ materialCode = 'MAT-100001'; quantity = 2 }); assistantSessionRef = 'demo'
} ($auth + @{ 'Idempotency-Key' = [guid]::NewGuid().ToString() })
$created | ConvertTo-Json -Depth 10

Step '9. A person approves it; the integration transfers it; the assistant reads the status'
Post "$src/_admin/requisitions/$($created.id)/edit" @{ status = 'Approved'; approvedAt = (Get-Date).ToUniversalTime().ToString('o') } | Out-Null
Write-Host "  $($created.id) approved."
procsync run | Out-Null
Invoke-RestMethod -Uri "$assistant/v1/requisition-requests/$($created.id)" -Headers $auth | ConvertTo-Json -Depth 10

Step '10. Reconciliation: prove nothing was lost or duplicated'
procsync reconcile
Write-Host ''
Write-Host "  Open the HTML report: $root/reports/latest.html"
Write-Host '  Stop the services with: scripts/stop-all.ps1'
