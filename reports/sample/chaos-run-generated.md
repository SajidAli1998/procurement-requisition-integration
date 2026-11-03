# Reconciliation report — run-20261103-070536-719675e9b

**Status:** Completed  
**Trigger:** cli · **Started:** 2026-11-03 07:05:36Z · **Generated:** 2026-11-03 07:06:26Z

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
| Created | 181 |
| InDoubt | 3 |
| InDoubtResolved | 3 |
| WrittenBack | 184 |
| sap.retries | 68 |
| sap.throttled | 26 |
| retry wait (s) | 46.1 |

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
| NOID-C9A925B92E6F2F70 | 1 | Validate | MissingRequiredField | id is missing | The source sent a requisition without an id. Raise with the source system owner; it cannot be matched or transferred. | 1 |
| REQ-2026-000116 | 2 | Validate | MissingRequiredField | requester is missing | Fill in the missing field in the source system; the next run picks it up automatically. | 1 |
| REQ-2026-000076 | 1 | Validate | NeededByInPast | neededBy 2026-10-24 is in the past | Move the needed-by date to today or later in the source system. | 1 |
| REQ-2026-000216 | 1 | Validate | NeededByInPast | neededBy 2026-10-24 is in the past | Move the needed-by date to today or later in the source system. | 1 |
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

## Notable events this run (29)

| Requisition | Event | Detail |
|---|---|---|
| REQ-2026-000076 | Quarantined | NeededByInPast: neededBy 2026-10-24 is in the past |
| REQ-2026-000158 | Quarantined | MissingLines: Requisition has no lines |
| REQ-2026-000204 | Quarantined | UnknownUnitOfMeasure: Line 10: unit of measure 'BUCKET' is not recognised |
| REQ-2026-000090 | Quarantined | SchemaInvalid: lines[0].quantity must be a number, got String "ten" |
| REQ-2026-000061 | Quarantined | UnknownUnitOfMeasure: Line 10: unit of measure 'BUCKET' is not recognised |
| REQ-2026-000188 | Quarantined | UnknownCurrency: Currency 'XYZ' is not a supported ISO 4217 code |
| REQ-2026-000029 | Quarantined | InvalidQuantity: Line 10: quantity -5 must be greater than zero |
| REQ-2026-000228 | Quarantined | SchemaInvalid: lines[0].quantity must be a number, got String "ten" |
| REQ-2026-000245 | Quarantined | TotalMismatch: totalAmount 7466.67 does not equal the sum of the lines (7366.67) |
| NOID-C9A925B92E6F2F70 | Quarantined | MissingRequiredField: id is missing |
| REQ-2026-000171 | Quarantined | InvalidQuantity: Line 10: quantity -5 must be greater than zero |
| REQ-2026-000216 | Quarantined | NeededByInPast: neededBy 2026-10-24 is in the past |
| REQ-2026-000045 | Quarantined | UnknownCurrency: Currency 'XYZ' is not a supported ISO 4217 code |
| REQ-2026-000103 | Quarantined | TotalMismatch: totalAmount 4288.96 does not equal the sum of the lines (4188.96) |
| REQ-2026-000014 | Quarantined | MissingLines: Requisition has no lines |
| REQ-2026-000129 | Quarantined | SchemaInvalid: neededBy '31/02/2026' is not a valid yyyy-MM-dd date |
| REQ-2026-000116 | Quarantined | MissingRequiredField: requester is missing |
| REQ-2026-000060 | Quarantined | SapRejectedPermanent: SAP rejected the requisition: Item 00010: plant 1100 is not assigned to company code 2000 |
| REQ-2026-000151 | Quarantined | SapRejectedPermanent: SAP rejected the requisition: Item 00010: plant 2100 is not assigned to company code 1000 |
| REQ-2026-000107 | Quarantined | SapRejectedPermanent: SAP rejected the requisition: Item 00010: plant 2100 is not assigned to company code 1000 |
| REQ-2026-000027 | InDoubt | HttpRequestException: An error occurred while sending the request. |
| REQ-2026-000027 | InDoubtResolved | Response was lost; SAP PR 0010000062 found on re-check |
| REQ-2026-000203 | Quarantined | SapRejectedPermanent: SAP rejected the requisition: Item 00010: plant 1100 is not assigned to company code 2000 |
| REQ-2026-000012 | InDoubt | HttpRequestException: An error occurred while sending the request. |
| REQ-2026-000012 | InDoubtResolved | Response was lost; SAP PR 0010000074 found on re-check |
| REQ-2026-000250 | Quarantined | SapRejectedPermanent: SAP rejected the requisition: Item 00010: plant 1100 is not assigned to company code 2000 |
| REQ-2026-000008 | Quarantined | SapRejectedPermanent: SAP rejected the requisition: Item 00010: plant 1100 is not assigned to company code 2000 |
| REQ-2026-000002 | InDoubt | HttpRequestException: An error occurred while sending the request. |
| REQ-2026-000002 | InDoubtResolved | Response was lost; SAP PR 0010000162 found on re-check |

## Per requisition (250)

**184** Created · **23** Quarantined · **43** Skipped

| Requisition | Ver | Result | SAP document | Attempts | Failure category | Explanation |
|---|---:|---|---|---:|---|---|
| NOID-C9A925B92E6F2F70 | 1 | Quarantined |  | 1 | Validation | id is missing |
| REQ-2026-000001 | 1 | Created | 0010000099 | 1 | None | Created successfully as 0010000099 |
| REQ-2026-000002 | 2 | Created | 0010000162 | 1 | Lost response | Created after one transient failure and after a lost response was confirmed by lookup as 0010000162 |
| REQ-2026-000003 | 1 | Skipped |  | 0 | Not approved | Source approval status is PendingApproval |
| REQ-2026-000004 | 2 | Created | 0010000082 | 1 | None | Created successfully as 0010000082 |
| REQ-2026-000005 | 1 | Created | 0010000171 | 1 | None | Created successfully as 0010000171 |
| REQ-2026-000006 | 2 | Created | 0010000131 | 1 | None | Created successfully as 0010000131 |
| REQ-2026-000007 | 1 | Created | 0010000006 | 1 | None | Created successfully as 0010000006 |
| REQ-2026-000008 | 3 | Quarantined |  | 1 | Rejected by SAP | SAP rejected the requisition: Item 00010: plant 1100 is not assigned to company code 2000 |
| REQ-2026-000009 | 3 | Created | 0010000159 | 1 | None | Created successfully as 0010000159 |
| REQ-2026-000010 | 3 | Created | 0010000150 | 1 | None | Created successfully as 0010000150 |
| REQ-2026-000011 | 1 | Skipped |  | 0 | Not approved | Source approval status is Draft |
| REQ-2026-000012 | 1 | Created | 0010000074 | 2 | Lost response | Created after two transient failures and after a lost response was confirmed by lookup as 0010000074 |
| REQ-2026-000013 | 3 | Created | 0010000101 | 1 | None | Created successfully as 0010000101 |
| REQ-2026-000014 | 2 | Quarantined |  | 1 | Validation | Requisition has no lines |
| REQ-2026-000015 | 2 | Created | 0010000030 | 1 | None | Created successfully as 0010000030 |
| REQ-2026-000016 | 2 | Skipped |  | 0 | Not approved | Source approval status is Cancelled |
| REQ-2026-000017 | 3 | Skipped |  | 0 | Not approved | Source approval status is PendingApproval |
| REQ-2026-000018 | 1 | Created | 0010000067 | 1 | None | Created successfully as 0010000067 |
| REQ-2026-000019 | 3 | Created | 0010000116 | 1 | None | Created successfully as 0010000116 |
| REQ-2026-000020 | 3 | Skipped |  | 0 | Not approved | Source approval status is Draft |
| REQ-2026-000021 | 3 | Created | 0010000002 | 1 | None | Created successfully as 0010000002 |
| REQ-2026-000022 | 3 | Created | 0010000090 | 1 | None | Created successfully as 0010000090 |
| REQ-2026-000023 | 3 | Created | 0010000042 | 1 | None | Created successfully as 0010000042 |
| REQ-2026-000024 | 3 | Created | 0010000120 | 1 | None | Created successfully as 0010000120 |
| REQ-2026-000025 | 1 | Created | 0010000078 | 1 | None | Created successfully as 0010000078 |
| REQ-2026-000026 | 2 | Created | 0010000077 | 1 | None | Created successfully as 0010000077 |
| REQ-2026-000027 | 1 | Created | 0010000062 | 1 | Lost response | Created after one transient failure and after a lost response was confirmed by lookup as 0010000062 |
| REQ-2026-000028 | 2 | Created | 0010000011 | 2 | Transient service | Created after one transient failure as 0010000011 |
| REQ-2026-000029 | 1 | Quarantined |  | 1 | Validation | Line 10: quantity -5 must be greater than zero |
| REQ-2026-000030 | 2 | Created | 0010000061 | 1 | None | Created successfully as 0010000061 |
| REQ-2026-000031 | 1 | Created | 0010000172 | 1 | None | Created successfully as 0010000172 |
| REQ-2026-000032 | 1 | Created | 0010000152 | 1 | None | Created successfully as 0010000152 |
| REQ-2026-000033 | 1 | Created | 0010000115 | 1 | None | Created successfully as 0010000115 |
| REQ-2026-000034 | 1 | Created | 0010000130 | 1 | None | Created successfully as 0010000130 |
| REQ-2026-000035 | 2 | Skipped |  | 0 | Not approved | Source approval status is Rejected |
| REQ-2026-000036 | 2 | Created | 0010000020 | 1 | None | Created successfully as 0010000020 |
| REQ-2026-000037 | 3 | Created | 0010000089 | 1 | None | Created successfully as 0010000089 |
| REQ-2026-000038 | 3 | Created | 0010000051 | 1 | None | Created successfully as 0010000051 |
| REQ-2026-000039 | 1 | Created | 0010000118 | 1 | None | Created successfully as 0010000118 |
| REQ-2026-000040 | 2 | Skipped |  | 0 | Not approved | Source approval status is Draft |
| REQ-2026-000041 | 1 | Created | 0010000180 | 1 | None | Created successfully as 0010000180 |
| REQ-2026-000042 | 3 | Created | 0010000145 | 2 | Transient service | Created after one transient failure as 0010000145 |
| REQ-2026-000043 | 2 | Skipped |  | 0 | Not approved | Source approval status is Rejected |
| REQ-2026-000044 | 1 | Skipped |  | 0 | Not approved | Source approval status is PendingApproval |
| REQ-2026-000045 | 2 | Quarantined |  | 1 | Validation | Currency 'XYZ' is not a supported ISO 4217 code |
| REQ-2026-000046 | 1 | Created | 0010000068 | 1 | None | Created successfully as 0010000068 |
| REQ-2026-000047 | 3 | Created | 0010000144 | 1 | None | Created successfully as 0010000144 |
| REQ-2026-000048 | 3 | Skipped |  | 0 | Not approved | Source approval status is Rejected |
| REQ-2026-000049 | 2 | Skipped |  | 0 | Not approved | Source approval status is PendingApproval |
| REQ-2026-000050 | 1 | Created | 0010000167 | 1 | None | Created successfully as 0010000167 |
| REQ-2026-000051 | 2 | Created | 0010000048 | 3 | Rate limit | Created after one rate-limit response and after one transient failure as 0010000048 |
| REQ-2026-000052 | 2 | Created | 0010000040 | 2 | Rate limit | Created after one rate-limit response as 0010000040 |
| REQ-2026-000053 | 2 | Skipped |  | 0 | Not approved | Source approval status is Rejected |
| REQ-2026-000054 | 2 | Created | 0010000050 | 1 | None | Created successfully as 0010000050 |
| REQ-2026-000055 | 1 | Skipped |  | 0 | Not approved | Source approval status is Cancelled |
| REQ-2026-000056 | 1 | Created | 0010000036 | 1 | None | Created successfully as 0010000036 |
| REQ-2026-000057 | 1 | Created | 0010000151 | 1 | None | Created successfully as 0010000151 |
| REQ-2026-000058 | 1 | Created | 0010000043 | 1 | None | Created successfully as 0010000043 |
| REQ-2026-000059 | 2 | Created | 0010000063 | 1 | None | Created successfully as 0010000063 |
| REQ-2026-000060 | 2 | Quarantined |  | 3 | Rejected by SAP | SAP rejected the requisition: Item 00010: plant 1100 is not assigned to company code 2000 |
| REQ-2026-000061 | 1 | Quarantined |  | 1 | Validation | Line 10: unit of measure 'BUCKET' is not recognised |
| REQ-2026-000062 | 3 | Created | 0010000163 | 1 | None | Created successfully as 0010000163 |
| REQ-2026-000063 | 2 | Skipped |  | 0 | Not approved | Source approval status is PendingApproval |
| REQ-2026-000064 | 2 | Created | 0010000029 | 1 | None | Created successfully as 0010000029 |
| REQ-2026-000065 | 3 | Created | 0010000100 | 1 | None | Created successfully as 0010000100 |
| REQ-2026-000066 | 1 | Created | 0010000148 | 1 | None | Created successfully as 0010000148 |
| REQ-2026-000067 | 3 | Created | 0010000153 | 1 | None | Created successfully as 0010000153 |
| REQ-2026-000068 | 3 | Skipped |  | 0 | Not approved | Source approval status is PendingApproval |
| REQ-2026-000069 | 2 | Created | 0010000049 | 1 | None | Created successfully as 0010000049 |
| REQ-2026-000070 | 3 | Created | 0010000047 | 2 | Transient service | Created after one transient failure as 0010000047 |
| REQ-2026-000071 | 1 | Created | 0010000014 | 1 | None | Created successfully as 0010000014 |
| REQ-2026-000072 | 2 | Created | 0010000046 | 1 | None | Created successfully as 0010000046 |
| REQ-2026-000073 | 1 | Created | 0010000039 | 1 | None | Created successfully as 0010000039 |
| REQ-2026-000074 | 2 | Created | 0010000017 | 1 | None | Created successfully as 0010000017 |
| REQ-2026-000075 | 2 | Skipped |  | 0 | Not approved | Source approval status is Cancelled |
| REQ-2026-000076 | 1 | Quarantined |  | 1 | Validation | neededBy 2026-10-24 is in the past |
| REQ-2026-000077 | 2 | Created | 0010000146 | 1 | None | Created successfully as 0010000146 |
| REQ-2026-000078 | 3 | Created | 0010000058 | 1 | None | Created successfully as 0010000058 |
| REQ-2026-000079 | 2 | Created | 0010000135 | 1 | None | Created successfully as 0010000135 |
| REQ-2026-000080 | 1 | Created | 0010000139 | 1 | None | Created successfully as 0010000139 |
| REQ-2026-000081 | 1 | Created | 0010000008 | 1 | None | Created successfully as 0010000008 |
| REQ-2026-000082 | 2 | Skipped |  | 0 | Not approved | Source approval status is Rejected |
| REQ-2026-000083 | 3 | Created | 0010000027 | 2 | Rate limit | Created after one rate-limit response as 0010000027 |
| REQ-2026-000084 | 2 | Created | 0010000132 | 1 | None | Created successfully as 0010000132 |
| REQ-2026-000085 | 1 | Created | 0010000057 | 1 | None | Created successfully as 0010000057 |
| REQ-2026-000086 | 1 | Created | 0010000149 | 2 | Rate limit | Created after one rate-limit response as 0010000149 |
| REQ-2026-000087 | 1 | Created | 0010000174 | 1 | None | Created successfully as 0010000174 |
| REQ-2026-000088 | 3 | Created | 0010000102 | 2 | Transient service | Created after one transient failure as 0010000102 |
| REQ-2026-000089 | 1 | Skipped |  | 0 | Not approved | Source approval status is Cancelled |
| REQ-2026-000090 | 1 | Quarantined |  | 1 | Validation | lines[0].quantity must be a number, got String "ten" |
| REQ-2026-000091 | 1 | Created | 0010000095 | 1 | None | Created successfully as 0010000095 |
| REQ-2026-000092 | 2 | Created | 0010000110 | 2 | Rate limit | Created after one rate-limit response as 0010000110 |
| REQ-2026-000093 | 1 | Created | 0010000181 | 1 | None | Created successfully as 0010000181 |
| REQ-2026-000094 | 2 | Created | 0010000094 | 1 | None | Created successfully as 0010000094 |
| REQ-2026-000095 | 2 | Created | 0010000107 | 1 | None | Created successfully as 0010000107 |
| REQ-2026-000096 | 1 | Created | 0010000154 | 1 | None | Created successfully as 0010000154 |
| REQ-2026-000097 | 3 | Created | 0010000134 | 1 | None | Created successfully as 0010000134 |
| REQ-2026-000098 | 1 | Created | 0010000106 | 1 | None | Created successfully as 0010000106 |
| REQ-2026-000099 | 3 | Created | 0010000184 | 3 | Rate limit | Created after two rate-limit responses as 0010000184 |
| REQ-2026-000100 | 1 | Skipped |  | 0 | Not approved | Source approval status is PendingApproval |
| REQ-2026-000101 | 1 | Created | 0010000166 | 1 | None | Created successfully as 0010000166 |
| REQ-2026-000102 | 1 | Created | 0010000141 | 2 | Transient service | Created after one transient failure as 0010000141 |
| REQ-2026-000103 | 2 | Quarantined |  | 1 | Validation | totalAmount 4288.96 does not equal the sum of the lines (4188.96) |
| REQ-2026-000104 | 3 | Skipped |  | 0 | Not approved | Source approval status is Rejected |
| REQ-2026-000105 | 2 | Created | 0010000129 | 1 | None | Created successfully as 0010000129 |
| REQ-2026-000106 | 2 | Created | 0010000133 | 1 | None | Created successfully as 0010000133 |
| REQ-2026-000107 | 2 | Quarantined |  | 1 | Rejected by SAP | SAP rejected the requisition: Item 00010: plant 2100 is not assigned to company code 1000 |
| REQ-2026-000108 | 2 | Created | 0010000028 | 1 | None | Created successfully as 0010000028 |
| REQ-2026-000109 | 3 | Created | 0010000155 | 2 | Transient service | Created after one transient failure as 0010000155 |
| REQ-2026-000110 | 3 | Created | 0010000022 | 1 | None | Created successfully as 0010000022 |
| REQ-2026-000111 | 3 | Created | 0010000121 | 1 | None | Created successfully as 0010000121 |
| REQ-2026-000112 | 3 | Created | 0010000092 | 1 | None | Created successfully as 0010000092 |
| REQ-2026-000113 | 3 | Created | 0010000018 | 1 | None | Created successfully as 0010000018 |
| REQ-2026-000114 | 2 | Created | 0010000142 | 2 | Transient service | Created after one transient failure as 0010000142 |
| REQ-2026-000115 | 3 | Created | 0010000009 | 1 | None | Created successfully as 0010000009 |
| REQ-2026-000116 | 2 | Quarantined |  | 1 | Validation | requester is missing |
| REQ-2026-000117 | 1 | Created | 0010000123 | 1 | None | Created successfully as 0010000123 |
| REQ-2026-000118 | 2 | Skipped |  | 0 | Not approved | Source approval status is PendingApproval |
| REQ-2026-000119 | 1 | Created | 0010000105 | 1 | None | Created successfully as 0010000105 |
| REQ-2026-000120 | 2 | Created | 0010000156 | 2 | Rate limit | Created after one rate-limit response as 0010000156 |
| REQ-2026-000121 | 1 | Created | 0010000179 | 1 | None | Created successfully as 0010000179 |
| REQ-2026-000122 | 1 | Created | 0010000081 | 1 | None | Created successfully as 0010000081 |
| REQ-2026-000123 | 2 | Created | 0010000175 | 2 | Transient service | Created after one transient failure as 0010000175 |
| REQ-2026-000124 | 2 | Created | 0010000044 | 1 | None | Created successfully as 0010000044 |
| REQ-2026-000125 | 3 | Created | 0010000137 | 1 | None | Created successfully as 0010000137 |
| REQ-2026-000126 | 1 | Created | 0010000168 | 1 | None | Created successfully as 0010000168 |
| REQ-2026-000127 | 1 | Created | 0010000031 | 1 | None | Created successfully as 0010000031 |
| REQ-2026-000128 | 3 | Created | 0010000059 | 1 | None | Created successfully as 0010000059 |
| REQ-2026-000129 | 1 | Quarantined |  | 1 | Validation | neededBy '31/02/2026' is not a valid yyyy-MM-dd date |
| REQ-2026-000130 | 1 | Created | 0010000001 | 1 | None | Created successfully as 0010000001 |
| REQ-2026-000131 | 1 | Created | 0010000103 | 1 | None | Created successfully as 0010000103 |
| REQ-2026-000132 | 3 | Created | 0010000005 | 2 | Rate limit | Created after one rate-limit response as 0010000005 |
| REQ-2026-000133 | 2 | Created | 0010000021 | 1 | None | Created successfully as 0010000021 |
| REQ-2026-000134 | 3 | Skipped |  | 0 | Not approved | Source approval status is Rejected |
| REQ-2026-000135 | 2 | Created | 0010000010 | 2 | Transient service | Created after one transient failure as 0010000010 |
| REQ-2026-000136 | 2 | Created | 0010000045 | 1 | None | Created successfully as 0010000045 |
| REQ-2026-000137 | 1 | Skipped |  | 0 | Not approved | Source approval status is Cancelled |
| REQ-2026-000138 | 2 | Created | 0010000114 | 3 | Rate limit | Created after two rate-limit responses as 0010000114 |
| REQ-2026-000139 | 1 | Created | 0010000119 | 2 | Transient service | Created after one transient failure as 0010000119 |
| REQ-2026-000140 | 2 | Skipped |  | 0 | Not approved | Source approval status is PendingApproval |
| REQ-2026-000141 | 2 | Created | 0010000086 | 1 | None | Created successfully as 0010000086 |
| REQ-2026-000142 | 2 | Created | 0010000125 | 1 | None | Created successfully as 0010000125 |
| REQ-2026-000143 | 3 | Created | 0010000084 | 1 | None | Created successfully as 0010000084 |
| REQ-2026-000145 | 2 | Created | 0010000158 | 1 | None | Created successfully as 0010000158 |
| REQ-2026-000146 | 1 | Created | 0010000096 | 1 | None | Created successfully as 0010000096 |
| REQ-2026-000147 | 3 | Created | 0010000019 | 2 | Transient service | Created after one transient failure as 0010000019 |
| REQ-2026-000148 | 1 | Created | 0010000104 | 1 | None | Created successfully as 0010000104 |
| REQ-2026-000149 | 3 | Created | 0010000113 | 1 | None | Created successfully as 0010000113 |
| REQ-2026-000150 | 3 | Created | 0010000083 | 1 | None | Created successfully as 0010000083 |
| REQ-2026-000151 | 3 | Quarantined |  | 1 | Rejected by SAP | SAP rejected the requisition: Item 00010: plant 2100 is not assigned to company code 1000 |
| REQ-2026-000152 | 1 | Skipped |  | 0 | Not approved | Source approval status is Rejected |
| REQ-2026-000153 | 1 | Created | 0010000088 | 1 | None | Created successfully as 0010000088 |
| REQ-2026-000154 | 2 | Skipped |  | 0 | Not approved | Source approval status is Draft |
| REQ-2026-000155 | 3 | Created | 0010000007 | 1 | None | Created successfully as 0010000007 |
| REQ-2026-000156 | 3 | Created | 0010000173 | 1 | None | Created successfully as 0010000173 |
| REQ-2026-000157 | 2 | Created | 0010000075 | 2 | Transient service | Created after one transient failure as 0010000075 |
| REQ-2026-000158 | 2 | Quarantined |  | 1 | Validation | Requisition has no lines |
| REQ-2026-000159 | 1 | Created | 0010000170 | 1 | None | Created successfully as 0010000170 |
| REQ-2026-000160 | 3 | Created | 0010000124 | 1 | None | Created successfully as 0010000124 |
| REQ-2026-000161 | 2 | Skipped |  | 0 | Not approved | Source approval status is Draft |
| REQ-2026-000162 | 2 | Created | 0010000108 | 1 | None | Created successfully as 0010000108 |
| REQ-2026-000163 | 1 | Created | 0010000140 | 1 | None | Created successfully as 0010000140 |
| REQ-2026-000164 | 3 | Created | 0010000053 | 1 | None | Created successfully as 0010000053 |
| REQ-2026-000165 | 1 | Created | 0010000023 | 1 | None | Created successfully as 0010000023 |
| REQ-2026-000166 | 1 | Created | 0010000065 | 1 | None | Created successfully as 0010000065 |
| REQ-2026-000167 | 1 | Created | 0010000080 | 1 | None | Created successfully as 0010000080 |
| REQ-2026-000168 | 3 | Created | 0010000066 | 1 | None | Created successfully as 0010000066 |
| REQ-2026-000169 | 1 | Created | 0010000091 | 1 | None | Created successfully as 0010000091 |
| REQ-2026-000170 | 3 | Created | 0010000136 | 1 | None | Created successfully as 0010000136 |
| REQ-2026-000171 | 1 | Quarantined |  | 1 | Validation | Line 10: quantity -5 must be greater than zero |
| REQ-2026-000172 | 1 | Skipped |  | 0 | Not approved | Source approval status is Cancelled |
| REQ-2026-000173 | 3 | Created | 0010000041 | 1 | None | Created successfully as 0010000041 |
| REQ-2026-000174 | 3 | Skipped |  | 0 | Not approved | Source approval status is Rejected |
| REQ-2026-000175 | 1 | Skipped |  | 0 | Not approved | Source approval status is Cancelled |
| REQ-2026-000176 | 2 | Created | 0010000143 | 1 | None | Created successfully as 0010000143 |
| REQ-2026-000177 | 2 | Skipped |  | 0 | Not approved | Source approval status is PendingApproval |
| REQ-2026-000178 | 3 | Created | 0010000069 | 2 | Rate limit | Created after one rate-limit response as 0010000069 |
| REQ-2026-000179 | 3 | Created | 0010000012 | 2 | Transient service | Created after one transient failure as 0010000012 |
| REQ-2026-000180 | 2 | Created | 0010000025 | 1 | None | Created successfully as 0010000025 |
| REQ-2026-000181 | 2 | Created | 0010000122 | 1 | None | Created successfully as 0010000122 |
| REQ-2026-000182 | 2 | Created | 0010000079 | 1 | None | Created successfully as 0010000079 |
| REQ-2026-000183 | 3 | Created | 0010000033 | 1 | None | Created successfully as 0010000033 |
| REQ-2026-000184 | 2 | Created | 0010000056 | 1 | None | Created successfully as 0010000056 |
| REQ-2026-000185 | 3 | Created | 0010000087 | 1 | None | Created successfully as 0010000087 |
| REQ-2026-000186 | 3 | Created | 0010000112 | 1 | None | Created successfully as 0010000112 |
| REQ-2026-000187 | 3 | Skipped |  | 0 | Not approved | Source approval status is Rejected |
| REQ-2026-000188 | 2 | Quarantined |  | 1 | Validation | Currency 'XYZ' is not a supported ISO 4217 code |
| REQ-2026-000189 | 3 | Skipped |  | 0 | Not approved | Source approval status is PendingApproval |
| REQ-2026-000190 | 2 | Skipped |  | 0 | Not approved | Source approval status is Draft |
| REQ-2026-000191 | 2 | Created | 0010000037 | 1 | None | Created successfully as 0010000037 |
| REQ-2026-000192 | 1 | Created | 0010000064 | 1 | None | Created successfully as 0010000064 |
| REQ-2026-000193 | 2 | Created | 0010000034 | 1 | None | Created successfully as 0010000034 |
| REQ-2026-000194 | 3 | Created | 0010000111 | 1 | None | Created successfully as 0010000111 |
| REQ-2026-000195 | 3 | Created | 0010000052 | 1 | None | Created successfully as 0010000052 |
| REQ-2026-000196 | 1 | Created | 0010000178 | 1 | None | Created successfully as 0010000178 |
| REQ-2026-000197 | 2 | Created | 0010000147 | 2 | Transient service | Created after one transient failure as 0010000147 |
| REQ-2026-000198 | 3 | Created | 0010000015 | 1 | None | Created successfully as 0010000015 |
| REQ-2026-000199 | 2 | Skipped |  | 0 | Not approved | Source approval status is Rejected |
| REQ-2026-000200 | 3 | Skipped |  | 0 | Not approved | Source approval status is PendingApproval |
| REQ-2026-000201 | 1 | Created | 0010000164 | 1 | None | Created successfully as 0010000164 |
| REQ-2026-000202 | 2 | Created | 0010000157 | 1 | None | Created successfully as 0010000157 |
| REQ-2026-000203 | 1 | Quarantined |  | 1 | Rejected by SAP | SAP rejected the requisition: Item 00010: plant 1100 is not assigned to company code 2000 |
| REQ-2026-000204 | 1 | Quarantined |  | 1 | Validation | Line 10: unit of measure 'BUCKET' is not recognised |
| REQ-2026-000205 | 3 | Created | 0010000071 | 1 | None | Created successfully as 0010000071 |
| REQ-2026-000206 | 3 | Created | 0010000055 | 2 | Transient service | Created after one transient failure as 0010000055 |
| REQ-2026-000207 | 3 | Created | 0010000127 | 1 | None | Created successfully as 0010000127 |
| REQ-2026-000208 | 3 | Created | 0010000004 | 2 | Rate limit | Created after one rate-limit response as 0010000004 |
| REQ-2026-000209 | 1 | Created | 0010000093 | 1 | None | Created successfully as 0010000093 |
| REQ-2026-000210 | 3 | Created | 0010000128 | 1 | None | Created successfully as 0010000128 |
| REQ-2026-000211 | 1 | Created | 0010000097 | 1 | None | Created successfully as 0010000097 |
| REQ-2026-000212 | 2 | Created | 0010000117 | 1 | None | Created successfully as 0010000117 |
| REQ-2026-000213 | 3 | Created | 0010000038 | 1 | None | Created successfully as 0010000038 |
| REQ-2026-000214 | 1 | Created | 0010000013 | 1 | None | Created successfully as 0010000013 |
| REQ-2026-000215 | 3 | Created | 0010000165 | 2 | Rate limit | Created after one rate-limit response as 0010000165 |
| REQ-2026-000216 | 1 | Quarantined |  | 1 | Validation | neededBy 2026-10-24 is in the past |
| REQ-2026-000217 | 2 | Created | 0010000072 | 1 | None | Created successfully as 0010000072 |
| REQ-2026-000218 | 3 | Created | 0010000098 | 1 | None | Created successfully as 0010000098 |
| REQ-2026-000219 | 1 | Created | 0010000177 | 1 | None | Created successfully as 0010000177 |
| REQ-2026-000220 | 3 | Created | 0010000073 | 2 | Transient service | Created after one transient failure as 0010000073 |
| REQ-2026-000221 | 2 | Created | 0010000060 | 1 | None | Created successfully as 0010000060 |
| REQ-2026-000222 | 2 | Created | 0010000076 | 1 | None | Created successfully as 0010000076 |
| REQ-2026-000223 | 2 | Created | 0010000054 | 1 | None | Created successfully as 0010000054 |
| REQ-2026-000224 | 3 | Created | 0010000182 | 1 | None | Created successfully as 0010000182 |
| REQ-2026-000225 | 2 | Created | 0010000160 | 1 | None | Created successfully as 0010000160 |
| REQ-2026-000226 | 1 | Created | 0010000109 | 1 | None | Created successfully as 0010000109 |
| REQ-2026-000227 | 2 | Created | 0010000176 | 2 | Transient service | Created after one transient failure as 0010000176 |
| REQ-2026-000228 | 3 | Quarantined |  | 1 | Validation | lines[0].quantity must be a number, got String "ten" |
| REQ-2026-000229 | 1 | Created | 0010000035 | 1 | None | Created successfully as 0010000035 |
| REQ-2026-000230 | 3 | Created | 0010000126 | 2 | Transient service | Created after one transient failure as 0010000126 |
| REQ-2026-000231 | 3 | Created | 0010000183 | 1 | None | Created successfully as 0010000183 |
| REQ-2026-000232 | 1 | Created | 0010000169 | 1 | None | Created successfully as 0010000169 |
| REQ-2026-000233 | 1 | Created | 0010000003 | 1 | None | Created successfully as 0010000003 |
| REQ-2026-000234 | 3 | Skipped |  | 0 | Not approved | Source approval status is Rejected |
| REQ-2026-000235 | 1 | Created | 0010000024 | 1 | None | Created successfully as 0010000024 |
| REQ-2026-000236 | 1 | Created | 0010000026 | 1 | None | Created successfully as 0010000026 |
| REQ-2026-000237 | 1 | Skipped |  | 0 | Not approved | Source approval status is PendingApproval |
| REQ-2026-000238 | 1 | Skipped |  | 0 | Not approved | Source approval status is Rejected |
| REQ-2026-000239 | 3 | Created | 0010000138 | 1 | None | Created successfully as 0010000138 |
| REQ-2026-000240 | 3 | Created | 0010000070 | 1 | None | Created successfully as 0010000070 |
| REQ-2026-000241 | 3 | Created | 0010000085 | 1 | None | Created successfully as 0010000085 |
| REQ-2026-000242 | 2 | Skipped |  | 0 | Not approved | Source approval status is Rejected |
| REQ-2026-000243 | 1 | Created | 0010000161 | 1 | None | Created successfully as 0010000161 |
| REQ-2026-000244 | 3 | Skipped |  | 0 | Not approved | Source approval status is PendingApproval |
| REQ-2026-000245 | 2 | Quarantined |  | 1 | Validation | totalAmount 7466.67 does not equal the sum of the lines (7366.67) |
| REQ-2026-000246 | 1 | Skipped |  | 0 | Not approved | Source approval status is Draft |
| REQ-2026-000247 | 3 | Created | 0010000032 | 1 | None | Created successfully as 0010000032 |
| REQ-2026-000248 | 1 | Skipped |  | 0 | Not approved | Source approval status is Cancelled |
| REQ-2026-000249 | 2 | Created | 0010000016 | 1 | None | Created successfully as 0010000016 |
| REQ-2026-000250 | 2 | Quarantined |  | 1 | Rejected by SAP | SAP rejected the requisition: Item 00010: plant 1100 is not assigned to company code 2000 |

## Waiting for retry (0)

None.
