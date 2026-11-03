# Reconciliation report — run-20261103-070336-7fd75f2e2

**Status:** Completed  
**Trigger:** cli · **Started:** 2026-11-03 07:03:37Z · **Generated:** 2026-11-03 07:04:48Z

## Invariants

| | Check | Detail |
|---|---|---|
| ✅ | Every approved requisition is accounted for | 115 approved: 65 Synced, 50 Quarantined. |
| ✅ | No duplicate requisitions in SAP | 65 SAP requisitions, each with a unique external reference. |
| ✅ | Every synced requisition exists in SAP | All 65 linked SAP numbers found. |
| ✅ | Every SAP requisition is linked to a source requisition | No unlinked SAP requisitions. |
| ✅ | Amounts match between source and SAP | QAR 3,881,360.00 in 65. |
| ✅ | Source shows the current SAP status | All ERP statuses are written back to the source. |

## This run

| Event | Count |
|---|---:|
| Extracted | 120 |
| Staged | 72 |
| Ignored | 5 |
| Quarantined | 50 |
| Created | 58 |
| AlreadyInSap | 7 |
| WrittenBack | 65 |
| VersionRefreshed | 7 |
| sap.retries | 28 |
| sap.throttled | 14 |
| retry wait (s) | 286.38 |

## Where everything stands

| State | Requisitions |
|---|---:|
| Ignored | 5 |
| Synced | 65 |
| Quarantined | 50 |

## Amounts (source vs SAP)

| Currency | Source count | Source amount | SAP count | SAP amount | Match |
|---|---:|---:|---:|---:|---|
| QAR | 65 | 3,881,360.00 | 65 | 3,881,360.00 | ✅ |

## Open failures (50)

| Requisition | Ver | Stage | Reason | What is wrong | What to do | Seen |
|---|---:|---|---|---|---|---:|
| REQ-7071 | 3 | Validate | AmountNotPositive | Total amount must be greater than zero (got 0) | Enter the approved total amount (greater than zero) in the source system; the next run picks it up. | 1 |
| REQ-7073 | 1 | Validate | AmountNotPositive | Total amount must be greater than zero (got 0) | Enter the approved total amount (greater than zero) in the source system; the next run picks it up. | 1 |
| REQ-7075 | 3 | Validate | AmountNotPositive | Total amount must be greater than zero (got 0) | Enter the approved total amount (greater than zero) in the source system; the next run picks it up. | 1 |
| REQ-7077 | 1 | Validate | AmountNotPositive | Total amount must be greater than zero (got 0) | Enter the approved total amount (greater than zero) in the source system; the next run picks it up. | 1 |
| REQ-7079 | 3 | Validate | AmountNotPositive | Total amount must be greater than zero (got 0) | Enter the approved total amount (greater than zero) in the source system; the next run picks it up. | 1 |
| REQ-7081 | 1 | Validate | AmountNotPositive | Total amount must be greater than zero (got 0) | Enter the approved total amount (greater than zero) in the source system; the next run picks it up. | 1 |
| REQ-7083 | 3 | Validate | AmountNotPositive | Total amount must be greater than zero (got 0) | Enter the approved total amount (greater than zero) in the source system; the next run picks it up. | 1 |
| REQ-7085 | 1 | Validate | AmountNotPositive | Total amount must be greater than zero (got 0) | Enter the approved total amount (greater than zero) in the source system; the next run picks it up. | 1 |
| REQ-7087 | 3 | Validate | AmountNotPositive | Total amount must be greater than zero (got 0) | Enter the approved total amount (greater than zero) in the source system; the next run picks it up. | 1 |
| REQ-7089 | 1 | Validate | AmountNotPositive | Total amount must be greater than zero (got 0) | Enter the approved total amount (greater than zero) in the source system; the next run picks it up. | 1 |
| REQ-7091 | 3 | Validate | AmountNotPositive | Total amount must be greater than zero (got 0) | Enter the approved total amount (greater than zero) in the source system; the next run picks it up. | 1 |
| REQ-7093 | 1 | Validate | AmountNotPositive | Total amount must be greater than zero (got 0) | Enter the approved total amount (greater than zero) in the source system; the next run picks it up. | 1 |
| REQ-7095 | 3 | Validate | AmountNotPositive | Total amount must be greater than zero (got 0) | Enter the approved total amount (greater than zero) in the source system; the next run picks it up. | 1 |
| REQ-7097 | 1 | Validate | AmountNotPositive | Total amount must be greater than zero (got 0) | Enter the approved total amount (greater than zero) in the source system; the next run picks it up. | 1 |
| REQ-7099 | 3 | Validate | AmountNotPositive | Total amount must be greater than zero (got 0) | Enter the approved total amount (greater than zero) in the source system; the next run picks it up. | 1 |
| REQ-7101 | 1 | Validate | AmountNotPositive | Total amount must be greater than zero (got 0) | Enter the approved total amount (greater than zero) in the source system; the next run picks it up. | 1 |
| REQ-7103 | 3 | Validate | AmountNotPositive | Total amount must be greater than zero (got 0) | Enter the approved total amount (greater than zero) in the source system; the next run picks it up. | 1 |
| REQ-7105 | 1 | Validate | AmountNotPositive | Total amount must be greater than zero (got 0) | Enter the approved total amount (greater than zero) in the source system; the next run picks it up. | 1 |
| REQ-7107 | 3 | Validate | AmountNotPositive | Total amount must be greater than zero (got 0) | Enter the approved total amount (greater than zero) in the source system; the next run picks it up. | 1 |
| REQ-7109 | 1 | Validate | AmountNotPositive | Total amount must be greater than zero (got 0) | Enter the approved total amount (greater than zero) in the source system; the next run picks it up. | 1 |
| REQ-7111 | 3 | Validate | AmountNotPositive | Total amount must be greater than zero (got 0) | Enter the approved total amount (greater than zero) in the source system; the next run picks it up. | 1 |
| REQ-7113 | 1 | Validate | AmountNotPositive | Total amount must be greater than zero (got 0) | Enter the approved total amount (greater than zero) in the source system; the next run picks it up. | 1 |
| REQ-7115 | 3 | Validate | AmountNotPositive | Total amount must be greater than zero (got 0) | Enter the approved total amount (greater than zero) in the source system; the next run picks it up. | 1 |
| REQ-7117 | 1 | Validate | AmountNotPositive | Total amount must be greater than zero (got 0) | Enter the approved total amount (greater than zero) in the source system; the next run picks it up. | 1 |
| REQ-7119 | 3 | Validate | AmountNotPositive | Total amount must be greater than zero (got 0) | Enter the approved total amount (greater than zero) in the source system; the next run picks it up. | 1 |
| REQ-7072 | 4 | Validate | MissingRequiredField | neededBy is missing | Fill in the missing field in the source system; the next run picks it up automatically. | 1 |
| REQ-7074 | 2 | Validate | MissingRequiredField | neededBy is missing | Fill in the missing field in the source system; the next run picks it up automatically. | 1 |
| REQ-7076 | 4 | Validate | MissingRequiredField | neededBy is missing | Fill in the missing field in the source system; the next run picks it up automatically. | 1 |
| REQ-7078 | 2 | Validate | MissingRequiredField | neededBy is missing | Fill in the missing field in the source system; the next run picks it up automatically. | 1 |
| REQ-7080 | 4 | Validate | MissingRequiredField | neededBy is missing | Fill in the missing field in the source system; the next run picks it up automatically. | 1 |
| REQ-7082 | 2 | Validate | MissingRequiredField | neededBy is missing | Fill in the missing field in the source system; the next run picks it up automatically. | 1 |
| REQ-7084 | 4 | Validate | MissingRequiredField | neededBy is missing | Fill in the missing field in the source system; the next run picks it up automatically. | 1 |
| REQ-7086 | 2 | Validate | MissingRequiredField | neededBy is missing | Fill in the missing field in the source system; the next run picks it up automatically. | 1 |
| REQ-7088 | 4 | Validate | MissingRequiredField | neededBy is missing | Fill in the missing field in the source system; the next run picks it up automatically. | 1 |
| REQ-7090 | 2 | Validate | MissingRequiredField | neededBy is missing | Fill in the missing field in the source system; the next run picks it up automatically. | 1 |
| REQ-7092 | 4 | Validate | MissingRequiredField | neededBy is missing | Fill in the missing field in the source system; the next run picks it up automatically. | 1 |
| REQ-7094 | 2 | Validate | MissingRequiredField | neededBy is missing | Fill in the missing field in the source system; the next run picks it up automatically. | 1 |
| REQ-7096 | 4 | Validate | MissingRequiredField | neededBy is missing | Fill in the missing field in the source system; the next run picks it up automatically. | 1 |
| REQ-7098 | 2 | Validate | MissingRequiredField | neededBy is missing | Fill in the missing field in the source system; the next run picks it up automatically. | 1 |
| REQ-7100 | 4 | Validate | MissingRequiredField | neededBy is missing | Fill in the missing field in the source system; the next run picks it up automatically. | 1 |
| REQ-7102 | 2 | Validate | MissingRequiredField | neededBy is missing | Fill in the missing field in the source system; the next run picks it up automatically. | 1 |
| REQ-7104 | 4 | Validate | MissingRequiredField | neededBy is missing | Fill in the missing field in the source system; the next run picks it up automatically. | 1 |
| REQ-7106 | 2 | Validate | MissingRequiredField | neededBy is missing | Fill in the missing field in the source system; the next run picks it up automatically. | 1 |
| REQ-7108 | 4 | Validate | MissingRequiredField | neededBy is missing | Fill in the missing field in the source system; the next run picks it up automatically. | 1 |
| REQ-7110 | 2 | Validate | MissingRequiredField | neededBy is missing | Fill in the missing field in the source system; the next run picks it up automatically. | 1 |
| REQ-7112 | 4 | Validate | MissingRequiredField | neededBy is missing | Fill in the missing field in the source system; the next run picks it up automatically. | 1 |
| REQ-7114 | 2 | Validate | MissingRequiredField | neededBy is missing | Fill in the missing field in the source system; the next run picks it up automatically. | 1 |
| REQ-7116 | 4 | Validate | MissingRequiredField | neededBy is missing | Fill in the missing field in the source system; the next run picks it up automatically. | 1 |
| REQ-7118 | 2 | Validate | MissingRequiredField | neededBy is missing | Fill in the missing field in the source system; the next run picks it up automatically. | 1 |
| REQ-7120 | 4 | Validate | MissingRequiredField | neededBy is missing | Fill in the missing field in the source system; the next run picks it up automatically. | 1 |

## Notable events this run (50)

| Requisition | Event | Detail |
|---|---|---|
| REQ-7071 | Quarantined | AmountNotPositive: Total amount must be greater than zero (got 0) |
| REQ-7072 | Quarantined | MissingRequiredField: neededBy is missing |
| REQ-7073 | Quarantined | AmountNotPositive: Total amount must be greater than zero (got 0) |
| REQ-7074 | Quarantined | MissingRequiredField: neededBy is missing |
| REQ-7075 | Quarantined | AmountNotPositive: Total amount must be greater than zero (got 0) |
| REQ-7076 | Quarantined | MissingRequiredField: neededBy is missing |
| REQ-7077 | Quarantined | AmountNotPositive: Total amount must be greater than zero (got 0) |
| REQ-7078 | Quarantined | MissingRequiredField: neededBy is missing |
| REQ-7079 | Quarantined | AmountNotPositive: Total amount must be greater than zero (got 0) |
| REQ-7080 | Quarantined | MissingRequiredField: neededBy is missing |
| REQ-7081 | Quarantined | AmountNotPositive: Total amount must be greater than zero (got 0) |
| REQ-7082 | Quarantined | MissingRequiredField: neededBy is missing |
| REQ-7083 | Quarantined | AmountNotPositive: Total amount must be greater than zero (got 0) |
| REQ-7084 | Quarantined | MissingRequiredField: neededBy is missing |
| REQ-7085 | Quarantined | AmountNotPositive: Total amount must be greater than zero (got 0) |
| REQ-7086 | Quarantined | MissingRequiredField: neededBy is missing |
| REQ-7087 | Quarantined | AmountNotPositive: Total amount must be greater than zero (got 0) |
| REQ-7088 | Quarantined | MissingRequiredField: neededBy is missing |
| REQ-7089 | Quarantined | AmountNotPositive: Total amount must be greater than zero (got 0) |
| REQ-7090 | Quarantined | MissingRequiredField: neededBy is missing |
| REQ-7091 | Quarantined | AmountNotPositive: Total amount must be greater than zero (got 0) |
| REQ-7092 | Quarantined | MissingRequiredField: neededBy is missing |
| REQ-7093 | Quarantined | AmountNotPositive: Total amount must be greater than zero (got 0) |
| REQ-7094 | Quarantined | MissingRequiredField: neededBy is missing |
| REQ-7095 | Quarantined | AmountNotPositive: Total amount must be greater than zero (got 0) |
| REQ-7096 | Quarantined | MissingRequiredField: neededBy is missing |
| REQ-7097 | Quarantined | AmountNotPositive: Total amount must be greater than zero (got 0) |
| REQ-7098 | Quarantined | MissingRequiredField: neededBy is missing |
| REQ-7099 | Quarantined | AmountNotPositive: Total amount must be greater than zero (got 0) |
| REQ-7100 | Quarantined | MissingRequiredField: neededBy is missing |
| REQ-7101 | Quarantined | AmountNotPositive: Total amount must be greater than zero (got 0) |
| REQ-7102 | Quarantined | MissingRequiredField: neededBy is missing |
| REQ-7103 | Quarantined | AmountNotPositive: Total amount must be greater than zero (got 0) |
| REQ-7104 | Quarantined | MissingRequiredField: neededBy is missing |
| REQ-7105 | Quarantined | AmountNotPositive: Total amount must be greater than zero (got 0) |
| REQ-7106 | Quarantined | MissingRequiredField: neededBy is missing |
| REQ-7107 | Quarantined | AmountNotPositive: Total amount must be greater than zero (got 0) |
| REQ-7108 | Quarantined | MissingRequiredField: neededBy is missing |
| REQ-7109 | Quarantined | AmountNotPositive: Total amount must be greater than zero (got 0) |
| REQ-7110 | Quarantined | MissingRequiredField: neededBy is missing |
| REQ-7111 | Quarantined | AmountNotPositive: Total amount must be greater than zero (got 0) |
| REQ-7112 | Quarantined | MissingRequiredField: neededBy is missing |
| REQ-7113 | Quarantined | AmountNotPositive: Total amount must be greater than zero (got 0) |
| REQ-7114 | Quarantined | MissingRequiredField: neededBy is missing |
| REQ-7115 | Quarantined | AmountNotPositive: Total amount must be greater than zero (got 0) |
| REQ-7116 | Quarantined | MissingRequiredField: neededBy is missing |
| REQ-7117 | Quarantined | AmountNotPositive: Total amount must be greater than zero (got 0) |
| REQ-7118 | Quarantined | MissingRequiredField: neededBy is missing |
| REQ-7119 | Quarantined | AmountNotPositive: Total amount must be greater than zero (got 0) |
| REQ-7120 | Quarantined | MissingRequiredField: neededBy is missing |

## Per requisition (120)

**58** Created · **7** Duplicate · **50** Quarantined · **5** Skipped

| Requisition | Ver | Result | SAP document | Attempts | Failure category | Explanation |
|---|---:|---|---|---:|---|---|
| REQ-7001 | 1 | Duplicate | MOCK-SAP-50001 | 1 | Idempotency | Already in SAP as MOCK-SAP-50001: existing document linked and not created again |
| REQ-7002 | 2 | Created | MOCK-SAP-50002 | 2 | Version conflict | Created after version refresh (v2 → v3) as MOCK-SAP-50002 |
| REQ-7003 | 3 | Created | MOCK-SAP-50003 | 3 | Rate limit | Created after two rate-limit responses as MOCK-SAP-50003 |
| REQ-7004 | 4 | Created | MOCK-SAP-50004 | 3 | Transient service | Created after two transient failures as MOCK-SAP-50004 |
| REQ-7005 | 1 | Created | MOCK-SAP-50005 | 1 | None | Created successfully as MOCK-SAP-50005 |
| REQ-7006 | 2 | Created | MOCK-SAP-50006 | 1 | None | Created successfully as MOCK-SAP-50006 |
| REQ-7007 | 3 | Created | MOCK-SAP-50007 | 1 | None | Created successfully as MOCK-SAP-50007 |
| REQ-7008 | 4 | Created | MOCK-SAP-50008 | 1 | None | Created successfully as MOCK-SAP-50008 |
| REQ-7009 | 1 | Created | MOCK-SAP-50009 | 1 | None | Created successfully as MOCK-SAP-50009 |
| REQ-7010 | 2 | Created | MOCK-SAP-50010 | 1 | None | Created successfully as MOCK-SAP-50010 |
| REQ-7011 | 3 | Duplicate | MOCK-SAP-50011 | 1 | Idempotency | Already in SAP as MOCK-SAP-50011: existing document linked and not created again |
| REQ-7012 | 4 | Created | MOCK-SAP-50012 | 2 | Version conflict | Created after version refresh (v4 → v5) as MOCK-SAP-50012 |
| REQ-7013 | 1 | Created | MOCK-SAP-50013 | 3 | Rate limit | Created after two rate-limit responses as MOCK-SAP-50013 |
| REQ-7014 | 2 | Created | MOCK-SAP-50014 | 3 | Transient service | Created after two transient failures as MOCK-SAP-50014 |
| REQ-7015 | 3 | Created | MOCK-SAP-50015 | 1 | None | Created successfully as MOCK-SAP-50015 |
| REQ-7016 | 4 | Created | MOCK-SAP-50016 | 1 | None | Created successfully as MOCK-SAP-50016 |
| REQ-7017 | 1 | Created | MOCK-SAP-50017 | 1 | None | Created successfully as MOCK-SAP-50017 |
| REQ-7018 | 2 | Created | MOCK-SAP-50018 | 1 | None | Created successfully as MOCK-SAP-50018 |
| REQ-7019 | 3 | Created | MOCK-SAP-50019 | 1 | None | Created successfully as MOCK-SAP-50019 |
| REQ-7020 | 4 | Created | MOCK-SAP-50020 | 1 | None | Created successfully as MOCK-SAP-50020 |
| REQ-7021 | 1 | Duplicate | MOCK-SAP-50021 | 1 | Idempotency | Already in SAP as MOCK-SAP-50021: existing document linked and not created again |
| REQ-7022 | 2 | Created | MOCK-SAP-50022 | 2 | Version conflict | Created after version refresh (v2 → v3) as MOCK-SAP-50022 |
| REQ-7023 | 3 | Created | MOCK-SAP-50023 | 3 | Rate limit | Created after two rate-limit responses as MOCK-SAP-50023 |
| REQ-7024 | 4 | Created | MOCK-SAP-50024 | 3 | Transient service | Created after two transient failures as MOCK-SAP-50024 |
| REQ-7025 | 1 | Created | MOCK-SAP-50025 | 1 | None | Created successfully as MOCK-SAP-50025 |
| REQ-7026 | 2 | Created | MOCK-SAP-50026 | 1 | None | Created successfully as MOCK-SAP-50026 |
| REQ-7027 | 3 | Created | MOCK-SAP-50027 | 1 | None | Created successfully as MOCK-SAP-50027 |
| REQ-7028 | 4 | Created | MOCK-SAP-50028 | 1 | None | Created successfully as MOCK-SAP-50028 |
| REQ-7029 | 1 | Created | MOCK-SAP-50029 | 1 | None | Created successfully as MOCK-SAP-50029 |
| REQ-7030 | 2 | Created | MOCK-SAP-50030 | 1 | None | Created successfully as MOCK-SAP-50030 |
| REQ-7031 | 3 | Duplicate | MOCK-SAP-50031 | 1 | Idempotency | Already in SAP as MOCK-SAP-50031: existing document linked and not created again |
| REQ-7032 | 4 | Created | MOCK-SAP-50032 | 2 | Version conflict | Created after version refresh (v4 → v5) as MOCK-SAP-50032 |
| REQ-7033 | 1 | Created | MOCK-SAP-50033 | 3 | Rate limit | Created after two rate-limit responses as MOCK-SAP-50033 |
| REQ-7034 | 2 | Created | MOCK-SAP-50034 | 3 | Transient service | Created after two transient failures as MOCK-SAP-50034 |
| REQ-7035 | 3 | Created | MOCK-SAP-50035 | 1 | None | Created successfully as MOCK-SAP-50035 |
| REQ-7036 | 4 | Created | MOCK-SAP-50036 | 1 | None | Created successfully as MOCK-SAP-50036 |
| REQ-7037 | 1 | Created | MOCK-SAP-50037 | 1 | None | Created successfully as MOCK-SAP-50037 |
| REQ-7038 | 2 | Created | MOCK-SAP-50038 | 1 | None | Created successfully as MOCK-SAP-50038 |
| REQ-7039 | 3 | Created | MOCK-SAP-50039 | 1 | None | Created successfully as MOCK-SAP-50039 |
| REQ-7040 | 4 | Created | MOCK-SAP-50040 | 1 | None | Created successfully as MOCK-SAP-50040 |
| REQ-7041 | 1 | Duplicate | MOCK-SAP-50041 | 1 | Idempotency | Already in SAP as MOCK-SAP-50041: existing document linked and not created again |
| REQ-7042 | 2 | Created | MOCK-SAP-50042 | 2 | Version conflict | Created after version refresh (v2 → v3) as MOCK-SAP-50042 |
| REQ-7043 | 3 | Created | MOCK-SAP-50043 | 3 | Rate limit | Created after two rate-limit responses as MOCK-SAP-50043 |
| REQ-7044 | 4 | Created | MOCK-SAP-50044 | 3 | Transient service | Created after two transient failures as MOCK-SAP-50044 |
| REQ-7045 | 1 | Created | MOCK-SAP-50045 | 1 | None | Created successfully as MOCK-SAP-50045 |
| REQ-7046 | 2 | Created | MOCK-SAP-50046 | 1 | None | Created successfully as MOCK-SAP-50046 |
| REQ-7047 | 3 | Created | MOCK-SAP-50047 | 1 | None | Created successfully as MOCK-SAP-50047 |
| REQ-7048 | 4 | Created | MOCK-SAP-50048 | 1 | None | Created successfully as MOCK-SAP-50048 |
| REQ-7049 | 1 | Created | MOCK-SAP-50049 | 1 | None | Created successfully as MOCK-SAP-50049 |
| REQ-7050 | 2 | Created | MOCK-SAP-50050 | 1 | None | Created successfully as MOCK-SAP-50050 |
| REQ-7051 | 3 | Duplicate | MOCK-SAP-50051 | 1 | Idempotency | Already in SAP as MOCK-SAP-50051: existing document linked and not created again |
| REQ-7052 | 4 | Created | MOCK-SAP-50052 | 2 | Version conflict | Created after version refresh (v4 → v5) as MOCK-SAP-50052 |
| REQ-7053 | 1 | Created | MOCK-SAP-50053 | 3 | Rate limit | Created after two rate-limit responses as MOCK-SAP-50053 |
| REQ-7054 | 2 | Created | MOCK-SAP-50054 | 3 | Transient service | Created after two transient failures as MOCK-SAP-50054 |
| REQ-7055 | 3 | Created | MOCK-SAP-50055 | 1 | None | Created successfully as MOCK-SAP-50055 |
| REQ-7056 | 4 | Created | MOCK-SAP-50056 | 1 | None | Created successfully as MOCK-SAP-50056 |
| REQ-7057 | 1 | Created | MOCK-SAP-50057 | 1 | None | Created successfully as MOCK-SAP-50057 |
| REQ-7058 | 2 | Created | MOCK-SAP-50058 | 1 | None | Created successfully as MOCK-SAP-50058 |
| REQ-7059 | 3 | Created | MOCK-SAP-50059 | 1 | None | Created successfully as MOCK-SAP-50059 |
| REQ-7060 | 4 | Created | MOCK-SAP-50060 | 1 | None | Created successfully as MOCK-SAP-50060 |
| REQ-7061 | 1 | Duplicate | MOCK-SAP-50061 | 1 | Idempotency | Already in SAP as MOCK-SAP-50061: existing document linked and not created again |
| REQ-7062 | 2 | Created | MOCK-SAP-50062 | 2 | Version conflict | Created after version refresh (v2 → v3) as MOCK-SAP-50062 |
| REQ-7063 | 3 | Created | MOCK-SAP-50063 | 3 | Rate limit | Created after two rate-limit responses as MOCK-SAP-50063 |
| REQ-7064 | 4 | Created | MOCK-SAP-50064 | 3 | Transient service | Created after two transient failures as MOCK-SAP-50064 |
| REQ-7065 | 1 | Created | MOCK-SAP-50065 | 1 | None | Created successfully as MOCK-SAP-50065 |
| REQ-7066 | 2 | Skipped |  | 0 | Not approved | Source approval status is PendingApproval |
| REQ-7067 | 3 | Skipped |  | 0 | Not approved | Source approval status is PendingApproval |
| REQ-7068 | 4 | Skipped |  | 0 | Not approved | Source approval status is PendingApproval |
| REQ-7069 | 1 | Skipped |  | 0 | Not approved | Source approval status is PendingApproval |
| REQ-7070 | 2 | Skipped |  | 0 | Not approved | Source approval status is PendingApproval |
| REQ-7071 | 3 | Quarantined |  | 1 | Validation | Total amount must be greater than zero (got 0) |
| REQ-7072 | 4 | Quarantined |  | 1 | Validation | neededBy is missing |
| REQ-7073 | 1 | Quarantined |  | 1 | Validation | Total amount must be greater than zero (got 0) |
| REQ-7074 | 2 | Quarantined |  | 1 | Validation | neededBy is missing |
| REQ-7075 | 3 | Quarantined |  | 1 | Validation | Total amount must be greater than zero (got 0) |
| REQ-7076 | 4 | Quarantined |  | 1 | Validation | neededBy is missing |
| REQ-7077 | 1 | Quarantined |  | 1 | Validation | Total amount must be greater than zero (got 0) |
| REQ-7078 | 2 | Quarantined |  | 1 | Validation | neededBy is missing |
| REQ-7079 | 3 | Quarantined |  | 1 | Validation | Total amount must be greater than zero (got 0) |
| REQ-7080 | 4 | Quarantined |  | 1 | Validation | neededBy is missing |
| REQ-7081 | 1 | Quarantined |  | 1 | Validation | Total amount must be greater than zero (got 0) |
| REQ-7082 | 2 | Quarantined |  | 1 | Validation | neededBy is missing |
| REQ-7083 | 3 | Quarantined |  | 1 | Validation | Total amount must be greater than zero (got 0) |
| REQ-7084 | 4 | Quarantined |  | 1 | Validation | neededBy is missing |
| REQ-7085 | 1 | Quarantined |  | 1 | Validation | Total amount must be greater than zero (got 0) |
| REQ-7086 | 2 | Quarantined |  | 1 | Validation | neededBy is missing |
| REQ-7087 | 3 | Quarantined |  | 1 | Validation | Total amount must be greater than zero (got 0) |
| REQ-7088 | 4 | Quarantined |  | 1 | Validation | neededBy is missing |
| REQ-7089 | 1 | Quarantined |  | 1 | Validation | Total amount must be greater than zero (got 0) |
| REQ-7090 | 2 | Quarantined |  | 1 | Validation | neededBy is missing |
| REQ-7091 | 3 | Quarantined |  | 1 | Validation | Total amount must be greater than zero (got 0) |
| REQ-7092 | 4 | Quarantined |  | 1 | Validation | neededBy is missing |
| REQ-7093 | 1 | Quarantined |  | 1 | Validation | Total amount must be greater than zero (got 0) |
| REQ-7094 | 2 | Quarantined |  | 1 | Validation | neededBy is missing |
| REQ-7095 | 3 | Quarantined |  | 1 | Validation | Total amount must be greater than zero (got 0) |
| REQ-7096 | 4 | Quarantined |  | 1 | Validation | neededBy is missing |
| REQ-7097 | 1 | Quarantined |  | 1 | Validation | Total amount must be greater than zero (got 0) |
| REQ-7098 | 2 | Quarantined |  | 1 | Validation | neededBy is missing |
| REQ-7099 | 3 | Quarantined |  | 1 | Validation | Total amount must be greater than zero (got 0) |
| REQ-7100 | 4 | Quarantined |  | 1 | Validation | neededBy is missing |
| REQ-7101 | 1 | Quarantined |  | 1 | Validation | Total amount must be greater than zero (got 0) |
| REQ-7102 | 2 | Quarantined |  | 1 | Validation | neededBy is missing |
| REQ-7103 | 3 | Quarantined |  | 1 | Validation | Total amount must be greater than zero (got 0) |
| REQ-7104 | 4 | Quarantined |  | 1 | Validation | neededBy is missing |
| REQ-7105 | 1 | Quarantined |  | 1 | Validation | Total amount must be greater than zero (got 0) |
| REQ-7106 | 2 | Quarantined |  | 1 | Validation | neededBy is missing |
| REQ-7107 | 3 | Quarantined |  | 1 | Validation | Total amount must be greater than zero (got 0) |
| REQ-7108 | 4 | Quarantined |  | 1 | Validation | neededBy is missing |
| REQ-7109 | 1 | Quarantined |  | 1 | Validation | Total amount must be greater than zero (got 0) |
| REQ-7110 | 2 | Quarantined |  | 1 | Validation | neededBy is missing |
| REQ-7111 | 3 | Quarantined |  | 1 | Validation | Total amount must be greater than zero (got 0) |
| REQ-7112 | 4 | Quarantined |  | 1 | Validation | neededBy is missing |
| REQ-7113 | 1 | Quarantined |  | 1 | Validation | Total amount must be greater than zero (got 0) |
| REQ-7114 | 2 | Quarantined |  | 1 | Validation | neededBy is missing |
| REQ-7115 | 3 | Quarantined |  | 1 | Validation | Total amount must be greater than zero (got 0) |
| REQ-7116 | 4 | Quarantined |  | 1 | Validation | neededBy is missing |
| REQ-7117 | 1 | Quarantined |  | 1 | Validation | Total amount must be greater than zero (got 0) |
| REQ-7118 | 2 | Quarantined |  | 1 | Validation | neededBy is missing |
| REQ-7119 | 3 | Quarantined |  | 1 | Validation | Total amount must be greater than zero (got 0) |
| REQ-7120 | 4 | Quarantined |  | 1 | Validation | neededBy is missing |

## Waiting for retry (0)

None.
