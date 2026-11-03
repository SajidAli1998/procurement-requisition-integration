# ADR-0009: The client's data pack is the default data set, read straight from Excel

**Status:** accepted

## Context
After the first version was built on our own generated data, the client supplied a synthetic data pack as an Excel workbook. It has requisitions and lines, a failure-injection schedule, the mock remote's expected state, and expected outcomes for quarantine, reconciliation and the AI API contract. The client said it could be used from Excel directly or imported into a database. Their mock remote also behaves differently from SAP in two ways: it answers a duplicate create with `409` and the existing document id, and it rejects a stale version with `409`.

## Decision
- **The workbook is the default data set** for the services, the demo and a dedicated test class. It is read at startup with Microsoft's Open XML SDK (`Procurement.SyntheticData`); there is no import step and no copy of the data in another format.
- **The mocks act out the workbook.** The source mock serves its requisitions unchanged. The SAP mock seeds the pre-existing documents, uses the workbook's document numbers and plays the failure schedule.
- **The integration handles the remote's signals, not the workbook.** Nothing in the integration knows about the workbook. It handles `409 DUPLICATE_REFERENCE` (adopt the existing document) and `409 VERSION_CONFLICT` (re-read the source, resend once, then quarantine) as general behaviours.
- **Expected-outcome sheets are test oracles.** Tests W01–W07 compare our results with them row by row. `procsync check-expected` does the same for a live run. The report gained a per-requisition table with the same columns as `Reconciliation_Expected`, also written as CSV.
- **Gaps are filled by documented assumptions** in [`data/README.md`](../../data/README.md): company and plant codes, the employee directory, and how the version conflict comes about.
- **The generated data set stays** for stress and edge-case scenarios the workbook does not cover: malformed JSON types, missing ids, SAP business rejections, page overlap and lost responses.

## Consequences
- What the client reviews is what runs: the demo's first run matches all 120 rows of `Reconciliation_Expected`.
- "Attempts" is defined precisely: SAP write attempts including transport retries, or 1 when a record is settled without a write (found already in SAP, or stopped by validation).
- The workbook's `Retry-After: 20` is honoured, so a first run takes about a minute. The demo raises parallelism and lowers the client-side rate to keep it short without skipping any wait.
- Two test fixtures now exist. Most scenario tests keep using the generated set, because their assertions depend on its specific defects.

## Alternatives considered
- **Import the workbook into SQL Server:** an extra moving part for reviewers, with no benefit for 120 rows.
- **Convert it to JSON once and commit that:** the copy could drift from the client's file, and the client would have to trust our conversion.
- **Replace the generated data entirely:** we would lose coverage of failure modes the workbook doesn't include.
