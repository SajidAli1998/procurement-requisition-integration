# Reconciliation report — reconcile-20261103-070510

**Status:** ReconciliationOnly  
**Trigger:** reconcile · **Started:** 2026-11-03 07:05:10Z · **Generated:** 2026-11-03 07:05:11Z

## Invariants

| | Check | Detail |
|---|---|---|
| ✅ | Every approved requisition is accounted for | 116 approved: 68 Synced, 48 Quarantined. |
| ✅ | No duplicate requisitions in SAP | 68 SAP requisitions, each with a unique external reference. |
| ✅ | Every synced requisition exists in SAP | All 68 linked SAP numbers found. |
| ✅ | Every SAP requisition is linked to a source requisition | No unlinked SAP requisitions. |
| ✅ | Amounts match between source and SAP | QAR 3,920,798.00 in 68. |
| ✅ | Source shows the current SAP status | All ERP statuses are written back to the source. |

## This run

| Event | Count |
|---|---:|
| retry wait (s) | 0 |

## Where everything stands

| State | Requisitions |
|---|---:|
| Ignored | 5 |
| Synced | 68 |
| Quarantined | 48 |

## Amounts (source vs SAP)

| Currency | Source count | Source amount | SAP count | SAP amount | Match |
|---|---:|---:|---:|---:|---|
| QAR | 68 | 3,920,798.00 | 68 | 3,920,798.00 | ✅ |

## Open failures (48)

| Requisition | Ver | Stage | Reason | What is wrong | What to do | Seen |
|---|---:|---|---|---|---|---:|
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

## Notable events this run (0)

None.

## Per requisition (0)

None.

## Waiting for retry (0)

None.
