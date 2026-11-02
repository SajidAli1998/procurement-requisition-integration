# Reconciliation report — run-20261102-120955-dbf2d0a55

**Status:** Completed  
**Trigger:** cli · **Started:** 2026-11-02 12:09:55Z · **Generated:** 2026-11-02 12:10:47Z

## Invariants

| | Check | Detail |
|---|---|---|
| ✅ | Every approved requisition is accounted for | 207 approved: 184 Synced, 23 Quarantined. |
| ✅ | No duplicate requisitions in SAP | 184 SAP requisitions, each with a unique external reference. |
| ✅ | Every synced requisition exists in SAP | All 184 linked SAP numbers found. |
| ✅ | Every SAP requisition is linked to a source requisition | No unlinked SAP requisitions. |
| ✅ | Amounts match between source and SAP | EUR 918,779.53 in 101; USD 821,094.87 in 83. |
| ✅ | Source shows the current SAP status | All ERP statuses are written back to the source. |

## This run

| Event | Count |
|---|---:|
| Extracted | 254 |
| Staged | 190 |
| Unchanged | 4 |
| Ignored | 43 |
| Quarantined | 23 |
| Created | 180 |
| InDoubt | 4 |
| InDoubtResolved | 4 |
| WrittenBack | 184 |
| sap.retries | 69 |
| sap.throttled | 23 |
| retry wait (s) | 44.61 |

## Where everything stands

| State | Requisitions |
|---|---:|
| Ignored | 43 |
| Synced | 184 |
| Quarantined | 23 |

## Amounts (source vs SAP)

| Currency | Source count | Source amount | SAP count | SAP amount | Match |
|---|---:|---:|---:|---:|---|
| EUR | 101 | 918,779.53 | 101 | 918,779.53 | ✅ |
| USD | 83 | 821,094.87 | 83 | 821,094.87 | ✅ |

## Open failures (23)

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
| REQ-2026-000045 | 2 | Validate | UnknownCurrency | Currency 'XYZ' is not a supported ISO 4217 code | Use a supported ISO 4217 currency, or add the currency to the integration's reference data if it is genuinely new. | 1 |
| REQ-2026-000188 | 2 | Validate | UnknownCurrency | Currency 'XYZ' is not a supported ISO 4217 code | Use a supported ISO 4217 currency, or add the currency to the integration's reference data if it is genuinely new. | 1 |
| REQ-2026-000061 | 1 | Validate | UnknownUnitOfMeasure | Line 10: unit of measure 'BUCKET' is not recognised | Use a supported unit of measure, or add it to the reference data if it is genuinely new. | 1 |
| REQ-2026-000204 | 1 | Validate | UnknownUnitOfMeasure | Line 10: unit of measure 'BUCKET' is not recognised | Use a supported unit of measure, or add it to the reference data if it is genuinely new. | 1 |

## Notable events this run (31)

| Requisition | Event | Detail |
|---|---|---|
| REQ-2026-000076 | Quarantined | NeededByInPast: neededBy 2026-10-23 is in the past |
| REQ-2026-000158 | Quarantined | MissingLines: Requisition has no lines |
| REQ-2026-000204 | Quarantined | UnknownUnitOfMeasure: Line 10: unit of measure 'BUCKET' is not recognised |
| REQ-2026-000090 | Quarantined | SchemaInvalid: lines[0].quantity must be a number, got String "ten" |
| REQ-2026-000061 | Quarantined | UnknownUnitOfMeasure: Line 10: unit of measure 'BUCKET' is not recognised |
| REQ-2026-000188 | Quarantined | UnknownCurrency: Currency 'XYZ' is not a supported ISO 4217 code |
| REQ-2026-000029 | Quarantined | InvalidQuantity: Line 10: quantity -5 must be greater than zero |
| REQ-2026-000228 | Quarantined | SchemaInvalid: lines[0].quantity must be a number, got String "ten" |
| REQ-2026-000245 | Quarantined | TotalMismatch: totalAmount 7466.67 does not equal the sum of the lines (7366.67) |
| NOID-748B4381FAD94C9C | Quarantined | MissingRequiredField: id is missing |
| REQ-2026-000171 | Quarantined | InvalidQuantity: Line 10: quantity -5 must be greater than zero |
| REQ-2026-000216 | Quarantined | NeededByInPast: neededBy 2026-10-23 is in the past |
| REQ-2026-000045 | Quarantined | UnknownCurrency: Currency 'XYZ' is not a supported ISO 4217 code |
| REQ-2026-000103 | Quarantined | TotalMismatch: totalAmount 4288.96 does not equal the sum of the lines (4188.96) |
| REQ-2026-000014 | Quarantined | MissingLines: Requisition has no lines |
| REQ-2026-000129 | Quarantined | SchemaInvalid: neededBy '31/02/2026' is not a valid yyyy-MM-dd date |
| REQ-2026-000116 | Quarantined | MissingRequiredField: requester is missing |
| REQ-2026-000060 | Quarantined | SapRejectedPermanent: SAP rejected the requisition: Item 00010: plant 1100 is not assigned to company code 2000 |
| REQ-2026-000113 | InDoubt | HttpRequestException: An error occurred while sending the request. |
| REQ-2026-000113 | InDoubtResolved | Response was lost; SAP PR 0010000019 found on re-check |
| REQ-2026-000249 | InDoubt | HttpRequestException: An error occurred while sending the request. |
| REQ-2026-000249 | InDoubtResolved | Response was lost; SAP PR 0010000016 found on re-check |
| REQ-2026-000151 | Quarantined | SapRejectedPermanent: SAP rejected the requisition: Item 00010: plant 2100 is not assigned to company code 1000 |
| REQ-2026-000107 | Quarantined | SapRejectedPermanent: SAP rejected the requisition: Item 00010: plant 2100 is not assigned to company code 1000 |
| REQ-2026-000203 | Quarantined | SapRejectedPermanent: SAP rejected the requisition: Item 00010: plant 1100 is not assigned to company code 2000 |
| REQ-2026-000250 | Quarantined | SapRejectedPermanent: SAP rejected the requisition: Item 00010: plant 1100 is not assigned to company code 2000 |
| REQ-2026-000008 | Quarantined | SapRejectedPermanent: SAP rejected the requisition: Item 00010: plant 1100 is not assigned to company code 2000 |
| REQ-2026-000005 | InDoubt | HttpRequestException: An error occurred while sending the request. |
| REQ-2026-000005 | InDoubtResolved | Response was lost; SAP PR 0010000169 found on re-check |
| REQ-2026-000156 | InDoubt | HttpRequestException: An error occurred while sending the request. |
| REQ-2026-000156 | InDoubtResolved | Response was lost; SAP PR 0010000172 found on re-check |

## Waiting for retry (0)

None.
