using Mocks.Shared.Faults;
using Procurement.Contracts;
using Procurement.Scenarios.Fixtures;
using Procurement.Scenarios.Lab;
using Procurement.Sync.Store;
using static Procurement.Scenarios.Engine.Priority;
using static Procurement.Scenarios.Engine.Mode;

namespace Procurement.Scenarios.Catalogue;

internal static class Approval
{
    private const string NoApprover = "The source contract carries approval status and time only: no approver identity, authority level, expiry, department master, vendor, budget or purchasing-group data. Approval authority is enforced by the source workflow; the integration cannot test what it is never given. Testing it would need a contract extension.";

    public static void Register(CatalogueBuilder c)
    {
        const string A = Categories.Approval;

        foreach (var (id, status) in new[] { ("APR-001", "Draft"), ("APR-002", "PendingApproval"), ("APR-003", "Rejected"), ("APR-004", "Cancelled") })
        {
            c.Add(id, A, $"{status} requisition is not transferred", High, Negative)
                .Expect($"Skipped with status {status}; nothing in SAP")
                .Run(async ctx =>
                {
                    var rid = "REQ-" + id;
                    await ctx.Lab.PutSourceAsync(rid, Req.Valid(rid, status: status));
                    await ctx.RunSyncAsync();
                    await ctx.ExpectSkippedAsync(rid, status);
                });
        }

        c.Add("APR-005", A, "Approved requisition is transferred", Critical, Functional)
            .Expect("Created once")
            .Run(async ctx =>
            {
                await ctx.Lab.PutSourceAsync("REQ-APR-005", Req.Valid("REQ-APR-005"));
                await ctx.RunSyncAsync();
                await ctx.ExpectCreatedOnceAsync("REQ-APR-005");
            });

        c.Add("APR-006", A, "Approval revoked while queued (SAP was down)", Critical, Negative, Recovery)
            .Describe("Staged while approved; SAP fails so it waits; then the approval is withdrawn. The queued create must not be sent.")
            .Sync(("Resilience:BreakerMinimumThroughput", "100"))
            .Expect("Withdrawn from the queue; skipped; nothing in SAP")
            .Run(async ctx =>
            {
                await ctx.Lab.PutSourceAsync("REQ-APR-006", Req.Valid("REQ-APR-006"));
                await ctx.Lab.ScriptSapAsync(Duplicates.SapCreate(FaultKind.Error, count: 5));
                await ctx.RunSyncAsync("SAP down: the create waits");
                ctx.Equal("waiting for retry", "RetryPending", (await ctx.Lab.RecordAsync("REQ-APR-006"))?.State.ToString());
                await ctx.Lab.EditSourceAsync("REQ-APR-006", new { status = "PendingApproval", approvedAt = (string?)null });
                ctx.Step("The approver withdraws the approval");
                await ctx.Lab.ClearFaultsAsync();
                await ctx.RunSyncAsync("SAP back");
                await ctx.ExpectSkippedAsync("REQ-APR-006", "PendingApproval");
            });

        c.Add("APR-007", A, "Approval revoked during a run, just before the create (re-check on)", Critical, Negative)
            .Describe("Advanced scenario C, first half. With ApprovalRecheck=Always the integration re-reads the requisition right before creating it; the approval is withdrawn between the SAP lookup and the create.")
            .Sync(("ApprovalRecheck", "Always"))
            .Expect("Re-check sees it is no longer approved: nothing created")
            .Run(async ctx =>
            {
                await ctx.Lab.PutSourceAsync("REQ-APR-007", Req.Valid("REQ-APR-007"));
                ctx.Lab.Hooks.Before("sap", LabHooks.IsSapLookup, () => ctx.Lab.EditSourceAsync("REQ-APR-007", new { status = "PendingApproval", approvedAt = (string?)null }));
                await ctx.RunSyncAsync();
                ctx.Check("hook fired between lookup and create", ctx.Lab.Hooks.Fired.Count == 1, 1, ctx.Lab.Hooks.Fired.Count);
                await ctx.ExpectSkippedAsync("REQ-APR-007");
            });

        c.Add("APR-007B", A, "Approval revoked after SAP committed the create (compensation policy)", Critical, Negative)
            .Describe("Advanced scenario C, second half. The default boundary re-checks only items that waited; here the approval is withdrawn in the instant after SAP created it. The SAP document exists and is not denied: the race is reported for a person to cancel it in SAP.")
            .Expect("SAP document kept and linked; quarantined SourceChangedAfterTransfer with a cancel-in-SAP action")
            .Run(async ctx =>
            {
                await ctx.Lab.PutSourceAsync("REQ-APR-007B", Req.Valid("REQ-APR-007B"));
                ctx.Lab.Hooks.Before("sap", LabHooks.IsSapCreate, () => ctx.Lab.EditSourceAsync("REQ-APR-007B", new { status = "Cancelled" }));
                await ctx.RunSyncAsync("create races the cancellation");
                await ctx.RunSyncAsync("next run sees the cancellation");
                ctx.Equal("SAP document exists (not hidden)", 1, await ctx.SapCountAsync("REQ-APR-007B"));
                var q = await ctx.Lab.OpenQuarantineAsync("REQ-APR-007B");
                ctx.Check("race reported as SourceChangedAfterTransfer", q.Any(e => e.Reason == QuarantineReason.SourceChangedAfterTransfer), "SourceChangedAfterTransfer", string.Join(",", q.Select(e => e.Reason)));
            });

        c.Add("APR-008", A, "Approved requisition modified after approval", High, Functional)
            .Expect("The latest approved content is what SAP holds (one document, amended)")
            .Run(async ctx =>
            {
                await ctx.Lab.PutSourceAsync("REQ-APR-008", Req.Valid("REQ-APR-008"));
                await ctx.RunSyncAsync();
                var v2 = Req.Valid("REQ-APR-008").Set("lines[0].quantity", 3).Recalculate();
                await ctx.Lab.EditSourceAsync("REQ-APR-008", new { lines = v2["lines"], totalAmount = v2["totalAmount"] });
                await ctx.RunSyncAsync();
                await ctx.ExpectCreatedOnceAsync("REQ-APR-008");
                ctx.Equal("SAP quantity", 3m, (await ctx.Lab.SapDocumentsForAsync("REQ-APR-008"))[0]["to_PurchaseReqnItem"]![0]!["RequestedQuantity"]!.GetValue<decimal>());
            });

        c.Add("APR-009", A, "Missing approval authority", Medium, Negative).Blocked(NoApprover);
        c.Add("APR-010", A, "Expired approval", Medium, Negative).Blocked(NoApprover);
        c.Add("APR-011", A, "Approver different from requester", Medium, Negative).Blocked(NoApprover);

        c.Add("APR-012", A, "Duplicate approvals (approved twice)", High, DuplicateStress)
            .Expect("Re-approval bumps the version without changing content: one document, no amendment")
            .Run(async ctx =>
            {
                await ctx.Lab.PutSourceAsync("REQ-APR-012", Req.Valid("REQ-APR-012"));
                await ctx.RunSyncAsync();
                await ctx.Lab.EditSourceAsync("REQ-APR-012", new { status = "Approved" });
                await ctx.Lab.EditSourceAsync("REQ-APR-012", new { status = "Approved" });
                var second = await ctx.RunSyncAsync("after two more approvals");
                ctx.Equal("amended", 0, second.Count(RunEventKind.Amended));
                await ctx.ExpectCreatedOnceAsync("REQ-APR-012");
            });

        FieldsAndFormats.Quarantine(c, "APR-013", A, "Approval timestamp in the future", High,
            r => r.Set("approvedAt", DateTime.UtcNow.AddDays(2).ToString("O")), QuarantineReason.InvalidApproval, "future");

        c.Add("APR-014", A, "Unrecognised approval status ('Approvedd')", High, Negative)
            .Expect("Never assumed approved: skipped")
            .Run(async ctx =>
            {
                await ctx.Lab.PutSourceAsync("REQ-APR-014", Req.Valid("REQ-APR-014").Set("status", "Approvedd"));
                await ctx.RunSyncAsync();
                await ctx.ExpectSkippedAsync("REQ-APR-014", "Approvedd");
            });

        c.Add("APR-015", A, "Requisition exceeds a spending limit", Medium, Negative)
            .Blocked("No spending-limit rule exists in the integration contract: it transfers what the source approved (VAL-004 shows a 2.45M request). The assistant API's 25,000 cap on AI drafts is tested in AI-006 and SEC-014.");
        c.Add("APR-016", A, "Requester in an invalid department", Low, Negative).Blocked(NoApprover);
        c.Add("APR-017", A, "Cost center inactive", Medium, Negative)
            .Blocked("The integration has no cost-center master data (only codes for plants, units and currencies). In production SAP rejects an inactive cost center, which is handled as SapRejectedPermanent (see SAP-013).");

        c.Add("APR-018", A, "Company code not permitted for the plant", High, Negative)
            .Describe("Plant 1100 belongs to company 1000. SAP refuses the combination; the integration does not retry it.")
            .Expect("SAP answers 400: quarantined SapRejectedPermanent after one attempt")
            .Run(async ctx =>
            {
                await ctx.Lab.PutSourceAsync("REQ-APR-018", Req.Valid("REQ-APR-018", company: "2000"));
                await ctx.RunSyncAsync();
                var q = await ctx.Lab.OpenQuarantineAsync("REQ-APR-018");
                ctx.Check("quarantined SapRejectedPermanent", q.Any(e => e.Reason == QuarantineReason.SapRejectedPermanent), "SapRejectedPermanent", string.Join(",", q.Select(e => e.Reason)));
                ctx.Equal("create attempts (no retry of a 400)", 1, ctx.Lab.Capture.Exchanges.Count(e => e.Method == "POST" && e.Url.EndsWith("A_PurchaseRequisitionHeader")));
            });

        c.Add("APR-019", A, "Purchasing group invalid", Low, Negative)
            .Blocked("Purchasing group is not in the source contract; the integration does not send one (SAP derives it). Nothing to validate.");
        FieldsAndFormats.Quarantine(c, "APR-020", A, "Plant not active / unknown (9100)", High, r => r.Set("lines[0].plant", "9100"), QuarantineReason.UnknownPlant, "plant");
        c.Add("APR-021", A, "Material blocked", Low, Negative).Blocked("No material master data reaches the integration; SAP would reject a blocked material (SapRejectedPermanent, see SAP-013).");
        c.Add("APR-022", A, "Vendor inactive", Low, Negative).Blocked(NoApprover);
        c.Add("APR-023", A, "Budget insufficient", Low, Negative).Blocked(NoApprover);
        c.Add("APR-024", A, "Policy changes while a batch is executing", Low, Negative)
            .Blocked("Validation rules and reference data are compiled into the integration; changing them needs a deployment, which cannot happen mid-run (a new process starts the next run).");
    }
}
