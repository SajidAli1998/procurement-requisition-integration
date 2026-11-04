#Requires -Version 7.0
# Guided demo of the whole solution on the client's synthetic data workbook (PowerShell 7+). Starts the services if needed.
#   scripts/demo.ps1            pauses between steps
#   scripts/demo.ps1 -NoPause   runs straight through
param([switch]$NoPause)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$src = 'http://localhost:5101'
$sap = 'http://localhost:5102'
$assistant = 'http://localhost:5103'

# The workbook's 429s ask for 20 seconds; more parallel sends keep the demo short while still honouring every wait.
# The client-side rate limit stays well under the SAP mock's own limit, so the only 429s are the workbook's.
$env:Sync__Parallelism = '8'
$env:Sync__Resilience__PermitsPerSecond = '8'
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
# Calls that are expected to fail: return the problem details instead of throwing.
function Try-Call([scriptblock]$call) {
    try { & $call } catch { "HTTP $([int]$_.Exception.Response.StatusCode): $($_.ErrorDetails.Message)" }
}
function Token([string]$user) { @{ Authorization = "Bearer $((Post "$assistant/dev/token" @{ user = $user }).access_token)" } }
function Outcomes([string[]]$ids) {
    Import-Csv reports/latest.csv | Where-Object { $ids -contains $_.Source_Requisition_ID } |
        Format-Table Source_Requisition_ID, Processing_Result, Remote_Document_ID, Attempts, Failure_Category, Explanation -AutoSize | Out-String -Width 200
}

& "$PSScriptRoot/run-all.ps1" | Out-Null

Step "1. Reset: the client's workbook in the source, SAP as the workbook describes it, no local state"
Post "$src/_admin/reset?dataset=Workbook" | Out-Null
Post "$sap/_admin/reset?dataset=Workbook" | Out-Null
Invoke-RestMethod -Method Delete -Uri "$src/_admin/faults" | Out-Null
Invoke-RestMethod -Method Delete -Uri "$sap/_admin/faults" | Out-Null
Remove-Item procsync.db, procsync.db-shm, procsync.db-wal -ErrorAction SilentlyContinue
Write-Host '  120 requisitions (115 approved, 5 pending), 50 of them defective.'
Write-Host '  SAP already holds 7 of them, and will answer 28 creates with 429, 503 or 409 (Failure_Injection sheet).'

Step '2. First sync run: three pages of 40, the workbook''s failures, Retry-After honoured (about a minute)'
procsync run

Step '3. Per requisition: what happened and why (also in reports/latest.csv)'
Outcomes @('REQ-7001', 'REQ-7002', 'REQ-7003', 'REQ-7004', 'REQ-7005', 'REQ-7066', 'REQ-7071', 'REQ-7072')
procsync check-expected

Step '4. Run again: nothing is created twice'
procsync run
$stats = Invoke-RestMethod "$sap/_admin/stats"
Write-Host ''
Write-Host "  SAP holds $($stats.total) requisitions; duplicates by external reference: $(@($stats.duplicateExternalReferences).Count)"

Step '5. What is in quarantine, and why?'
procsync quarantine list --reason AmountNotPositive | Select-Object -First 4
procsync quarantine list --reason MissingRequiredField | Select-Object -Skip 1 -First 3

Step '6. Someone fixes two quarantined requisitions in the source; the next run releases them'
Post "$src/_admin/requisitions/REQ-7071/edit" @{ totalAmount = 25452 } | Out-Null
Post "$src/_admin/requisitions/REQ-7072/edit" @{ neededBy = '2027-02-10' } | Out-Null
Write-Host '  REQ-7071: total 0 -> 25,452 (the sum of its lines);  REQ-7072: required-by date added'
procsync run
Outcomes @('REQ-7071', 'REQ-7072')

Step '7. Purchasing works the requisitions in SAP; statuses flow back to the source'
1..3 | ForEach-Object { Post "$sap/_admin/advance" | Out-Null }
procsync run | Out-Null
procsync status REQ-7005

Step '8. The AI assistant, signed in as different employees'
$needed = (Get-Date).ToUniversalTime().AddDays(30).ToString('yyyy-MM-dd')
$e1003 = Token 'E1003'; $e1001 = Token 'E1001'; $e1002 = Token 'E1002'; $e1008 = Token 'E1008'
Write-Host '  E1003 asks about their own REQ-7003:'
$own = Invoke-RestMethod -Uri "$assistant/v1/requisition-requests/REQ-7003" -Headers $e1003
Write-Host "    $($own.status): $($own.statusReason)"
Write-Host '  E1001 asks about REQ-7001, raised by a colleague:'
Write-Host "    $(Try-Call { Invoke-RestMethod -Uri "$assistant/v1/requisition-requests/REQ-7001" -Headers $e1001 })"
Write-Host '  E1002 validates a request with mistakes:'
Post "$assistant/v1/requisition-requests:validate" @{
    costCenter = 'CC-99'; plant = '1100'; neededBy = $needed; justification = 'Two route control displays for the depot'
    lines = @(@{ materialCode = 'SCREEN'; quantity = 2 })
} $e1002 | Select-Object -ExpandProperty issues | Format-Table field, code, suggestion -AutoSize | Out-String -Width 200
$body = @{
    costCenter = 'CC-02'; plant = '3100'; neededBy = $needed; justification = 'Two route control displays for the depot'
    lines = @(@{ materialCode = 'TEST-002-01'; quantity = 2 }); assistantSessionRef = 'demo'
}
$valid = Post "$assistant/v1/requisition-requests:validate" $body $e1002
Write-Host '  ...corrects it; validate returns a summary to show the user, and a confirmation token:'
Write-Host "    $($valid.confirmationSummary)"
Write-Host '  The user says yes; the assistant creates it with the token:'
$created = Post "$assistant/v1/requisition-requests" $body ($e1002 + @{ 'Idempotency-Key' = [guid]::NewGuid().ToString(); 'X-User-Confirmation' = $valid.confirmationToken })
Write-Host "    $($created.id): $($created.status)"
Write-Host '  E1008 may read but not raise requisitions:'
Write-Host "    $(Try-Call { Post "$assistant/v1/requisition-requests" $body ($e1008 + @{ 'Idempotency-Key' = 'demo-e1008' }) })"

Step '9. A person approves it; the integration transfers it; the assistant reads the status'
Post "$src/_admin/requisitions/$($created.id)/edit" @{ status = 'Approved'; approvedAt = (Get-Date).ToUniversalTime().ToString('o') } | Out-Null
Write-Host "  $($created.id) approved."
procsync run | Out-Null
$now = Invoke-RestMethod -Uri "$assistant/v1/requisition-requests/$($created.id)" -Headers $e1002
Write-Host "    $($now.status): $($now.statusReason)"

Step '10. Reconciliation: prove nothing was lost or duplicated'
procsync reconcile
Write-Host ''
Write-Host "  Open the HTML report: $root/reports/latest.html"
Write-Host '  Stop the services with: scripts/stop-all.ps1'
