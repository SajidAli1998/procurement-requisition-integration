# Reconciliation report — reconcile-20261102-121119

**Status:** ReconciliationOnly  
**Trigger:** reconcile · **Started:** 2026-11-02 12:11:19Z · **Generated:** 2026-11-02 12:11:19Z

## Invariants

| | Check | Detail |
|---|---|---|
| ✅ | Every approved requisition is accounted for | 208 approved: 186 Synced, 22 Quarantined. |
| ✅ | No duplicate requisitions in SAP | 186 SAP requisitions, each with a unique external reference. |
| ✅ | Every synced requisition exists in SAP | All 186 linked SAP numbers found. |
| ✅ | Every SAP requisition is linked to a source requisition | No unlinked SAP requisitions. |
| ✅ | Amounts match between source and SAP | EUR 918,779.53 in 101; USD 823,628.95 in 85. |
| ✅ | Source shows the current SAP status | All ERP statuses are written back to the source. |

## This run

| Event | Count |
|---|---:|
| retry wait (s) | 0 |

## Where everything stands

| State | Requisitions |
|---|---:|
| Ignored | 43 |
| Synced | 186 |
| Quarantined | 22 |

## Amounts (source vs SAP)

| Currency | Source count | Source amount | SAP count | SAP amount | Match |
|---|---:|---:|---:|---:|---|
| EUR | 101 | 918,779.53 | 101 | 918,779.53 | ✅ |
| USD | 85 | 823,628.95 | 85 | 823,628.95 | ✅ |

## Open failures (22)

| Requisition | Ver | Stage | Reason | What is wrong | What to do | Seen |
|---|---:|---|---|---|---|---:|
| REQ-2026-000029 | 1 | Validate | InvalidQuantity | Line 10: quantity -5 must be greater than zero | Correct the line quantity (must be greater than zero) in the source system. | 1 |
| REQ-2026-000171 | 1 | Validate | InvalidQuantity | Line 10: quantity -5 must be greater than zero | Correct the line quantity (must be greater than zero) in the source system. | 1 |
| REQ-2026-000014 | 2 | Validate | MissingLines | Requisition has no lines | Add at least one line to the requisition in the source system. | 1 |
| REQ-2026-000158 | 2 | Validate | MissingLines | Requisition has no lines | Add at least one line to the requisition in the source system. | 1 |
| NOID-748B4381FAD94C9C | 1 | Validate | MissingRequiredField | id is missing | The source sent a requisition without an id. Raise with the source system owner; it cannot be matched or transferred. | 1 |
| REQ-2026-000116 | 2 | Validate | MissingRequiredField | requester is missing | Fill in the missing field in the source system; the next run picks it up automatically. | 1 |
| REQ-2026-000076 | 1 | Validate | NeededByInPast | neededBy 2026-10-23 is in the past | Move the needed-by date to today or later in the source system. | 1 |
| REQ-2026-000216 | 1 | Validate | NeededByInPast | neededBy 2026-10-23 is in the past | Move the needed-by date to today or later in the source system. | 1 |
| REQ-2026-000008 | 3 | Load | SapRejectedPermanent | SAP rejected the requisition: Item 00010: plant 1100 is not assigned to company code 2000 | Correct the data SAP rejected (see summary) in the source, or fix the SAP configuration, then run: procsync quarantine replay REQ-2026-000008 | 1 |
| REQ-2026-000060 | 2 | Load | SapRejectedPermanent | SAP rejected the requisition: Item 00010: plant 1100 is not assigned to company code 2000 | Correct the data SAP rejected (see summary) in the source, or fix the SAP configuration, then run: procsync quarantine replay REQ-2026-000060 | 1 |
| REQ-2026-000107 | 2 | Load | SapRejectedPermanent | SAP rejected the requisition: Item 00010: plant 2100 is not assigned to company code 1000 | Correct the data SAP rejected (see summary) in the source, or fix the SAP configuration, then run: procsync quarantine replay REQ-2026-000107 | 1 |
| REQ-2026-000151 | 3 | Load | SapRejectedPermanent | SAP rejected the requisition: Item 00010: plant 2100 is not assigned to company code 1000 | Correct the data SAP rejected (see summary) in the source, or fix the SAP configuration, then run: procsync quarantine replay REQ-2026-000151 | 1 |
| REQ-2026-000203 | 1 | Load | SapRejectedPermanent | SAP rejected the requisition: Item 00010: plant 1100 is not assigned to company code 2000 | Correct the data SAP rejected (see summary) in the source, or fix the SAP configuration, then run: procsync quarantine replay REQ-2026-000203 | 1 |
| REQ-2026-000250 | 2 | Load | SapRejectedPermanent | SAP rejected the requisition: Item 00010: plant 1100 is not assigned to company code 2000 | Correct the data SAP rejected (see summary) in the source, or fix the SAP configuration, then run: procsync quarantine replay REQ-2026-000250 | 1 |
| REQ-2026-000090 | 1 | Validate | SchemaInvalid | lines[0].quantity must be a number, got String "ten" | Correct the field named in the summary in the source system; the next run picks up the corrected version automatically. | 1 |
| REQ-2026-000129 | 1 | Validate | SchemaInvalid | neededBy '31/02/2026' is not a valid yyyy-MM-dd date | Correct the field named in the summary in the source system; the next run picks up the corrected version automatically. | 1 |
| REQ-2026-000228 | 3 | Validate | SchemaInvalid | lines[0].quantity must be a number, got String "ten" | Correct the field named in the summary in the source system; the next run picks up the corrected version automatically. | 1 |
| REQ-2026-000103 | 2 | Validate | TotalMismatch | totalAmount 4288.96 does not equal the sum of the lines (4188.96) | Recalculate the header total so it equals the sum of the lines. | 1 |
| REQ-2026-000245 | 2 | Validate | TotalMismatch | totalAmount 7466.67 does not equal the sum of the lines (7366.67) | Recalculate the header total so it equals the sum of the lines. | 1 |
| REQ-2026-000188 | 2 | Validate | UnknownCurrency | Currency 'XYZ' is not a supported ISO 4217 code | Use a supported ISO 4217 currency, or add the currency to the integration's reference data if it is genuinely new. | 1 |
| REQ-2026-000061 | 1 | Validate | UnknownUnitOfMeasure | Line 10: unit of measure 'BUCKET' is not recognised | Use a supported unit of measure, or add it to the reference data if it is genuinely new. | 1 |
| REQ-2026-000204 | 1 | Validate | UnknownUnitOfMeasure | Line 10: unit of measure 'BUCKET' is not recognised | Use a supported unit of measure, or add it to the reference data if it is genuinely new. | 1 |

## Notable events this run (0)

None.

## Waiting for retry (0)

None.
