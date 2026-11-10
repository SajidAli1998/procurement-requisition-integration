using System.Text.Json.Nodes;
using Mocks.Shared.Faults;
using Procurement.Contracts;
using Procurement.Scenarios.Engine;
using Procurement.Scenarios.Fixtures;
using static Procurement.Scenarios.Engine.Priority;
using static Procurement.Scenarios.Engine.Mode;

namespace Procurement.Scenarios.Catalogue;

internal static class FieldsAndFormats
{
    internal static ScenarioBuilder Quarantine(CatalogueBuilder c, string id, string category, string title, Priority p,
        Func<JsonObject, JsonObject> bend, QuarantineReason reason, string? field, string? fixture = null)
    {
        var rid = "REQ-" + id;
        return c.Add(id, category, title, p, Negative)
            .Fixture(fixture ?? title).Expect($"Quarantined as {reason}{(field is null ? "" : $", naming '{field}'")}; raw payload kept; nothing sent to SAP",
                sap: "No document", db: "Record Quarantined with an open entry")
            .Run(async ctx =>
            {
                await ctx.Lab.PutSourceAsync(rid, bend(Req.Valid(rid)));
                await ctx.RunSyncAsync();
                await ctx.ExpectQuarantinedAsync(rid, reason, field);
                var posts = ctx.Lab.Capture.Exchanges.Count(e => e.Method == "POST" && e.Url.EndsWith("A_PurchaseRequisitionHeader"));
                ctx.Equal("create requests sent to SAP", 0, posts);
            });
    }

    /// <summary>A bent record that is still valid and must be created exactly once.</summary>
    internal static ScenarioBuilder Accepted(CatalogueBuilder c, string id, string category, string title, Priority p,
        Func<JsonObject, JsonObject> bend, string expectation, Func<ScenarioContext, JsonObject, Task>? also = null)
    {
        var rid = "REQ-" + id;
        return c.Add(id, category, title, p, Functional)
            .Fixture(title).Expect(expectation, sap: "One document")
            .Run(async ctx =>
            {
                await ctx.Lab.PutSourceAsync(rid, bend(Req.Valid(rid)));
                await ctx.RunSyncAsync();
                await ctx.ExpectCreatedOnceAsync(rid);
                var docs = await ctx.Lab.SapDocumentsForAsync(rid);
                if (also is not null && docs.Count > 0) await also(ctx, docs[0]);
            });
    }

    public static void Register(CatalogueBuilder c)
    {
        const string F = Categories.Fields;

        c.Add("FLD-001", F, "Missing requisition ID", Critical, Negative)
            .Expect("Quarantined under a content-hash key (NOID-…) as MissingRequiredField 'id'; cannot be matched or sent")
            .Run(async ctx =>
            {
                await ctx.Lab.PutSourceAsync("row-without-id", Req.Valid("x").Drop("id"));
                await ctx.RunSyncAsync();
                var entries = (await ctx.Lab.OpenQuarantineAsync()).Where(q => q.SourceKey.StartsWith("NOID-", StringComparison.Ordinal)).ToList();
                ctx.Equal("quarantine entries under a NOID key", 1, entries.Count);
                ctx.Check("reason MissingRequiredField on 'id'", entries.Any(e => e.Reason == QuarantineReason.MissingRequiredField && (e.Details ?? "").Contains("\"id\"")), "id", entries.FirstOrDefault()?.Details);
                ctx.Equal("SAP documents", 0, (await ctx.Lab.SapStatsAsync()).Total);
            });
        Quarantine(c, "FLD-002", F, "Missing requester", High, r => r.Drop("requester"), QuarantineReason.MissingRequiredField, "requester");
        Quarantine(c, "FLD-003", F, "Missing company code", High, r => r.Drop("companyCode"), QuarantineReason.MissingRequiredField, "companyCode");
        Quarantine(c, "FLD-004", F, "Missing cost center", High, r => r.Drop("costCenter"), QuarantineReason.MissingRequiredField, "costCenter");

        c.Add("FLD-005", F, "Missing approval status: never assumed approved", Critical, Negative)
            .Expect("Skipped as not approved; nothing sent to SAP")
            .Run(async ctx =>
            {
                await ctx.Lab.PutSourceAsync("REQ-FLD-005", Req.Valid("REQ-FLD-005").Drop("status"));
                await ctx.RunSyncAsync();
                await ctx.ExpectSkippedAsync("REQ-FLD-005");
            });

        Quarantine(c, "FLD-006", F, "Missing line items", High, r => r.Drop("lines"), QuarantineReason.MissingRequiredField, "lines");
        Accepted(c, "FLD-007", F, "Missing material code: free-text item", Medium, r => r.Set("lines[0].materialCode", null),
            "Accepted as a free-text item (material code is optional); SAP item has no material",
            async (ctx, doc) => ctx.Equal("SAP item material", null, doc["to_PurchaseReqnItem"]![0]!["Material"]?.GetValue<string>()));
        Quarantine(c, "FLD-008", F, "Missing quantity", High, r => r.Drop("lines[0].quantity"), QuarantineReason.MissingRequiredField, "quantity");
        Quarantine(c, "FLD-009", F, "Missing unit price (rejected; not looked up from a catalogue)", High, r => r.Drop("lines[0].unitPrice"), QuarantineReason.MissingRequiredField, "unitPrice")
            .Describe("Contract: the approved price is part of the approval. The integration never substitutes a catalogue price, so a missing price is a data error.");
        Quarantine(c, "FLD-010", F, "Missing currency", High, r => r.Drop("currency"), QuarantineReason.MissingRequiredField, "currency");
        Quarantine(c, "FLD-011", F, "Missing delivery date", High, r => r.Drop("neededBy"), QuarantineReason.MissingRequiredField, "neededBy");
        Quarantine(c, "FLD-012", F, "Missing plant code", High, r => r.Drop("lines[0].plant"), QuarantineReason.MissingRequiredField, "plant");
        Quarantine(c, "FLD-013", F, "Required field is null", High, r => r.Set("requester", null), QuarantineReason.MissingRequiredField, "requester");
        Quarantine(c, "FLD-014", F, "Required field is an empty string", High, r => r.Set("costCenter", ""), QuarantineReason.MissingRequiredField, "costCenter");
        Quarantine(c, "FLD-015", F, "Required field is whitespace only", High, r => r.Set("currency", "   "), QuarantineReason.MissingRequiredField, "currency");

        c.Add("FLD-016", F, "Several mandatory fields missing: every one reported", High, Negative)
            .Expect("One quarantine entry listing requester, costCenter, currency and neededBy")
            .Run(async ctx =>
            {
                await ctx.Lab.PutSourceAsync("REQ-FLD-016", Req.Valid("REQ-FLD-016").Drop("requester").Drop("costCenter").Drop("currency").Drop("neededBy"));
                await ctx.RunSyncAsync();
                var details = (await ctx.Lab.OpenQuarantineAsync("REQ-FLD-016")).FirstOrDefault()?.Details ?? "";
                foreach (var f in (string[])["requester", "costCenter", "currency", "neededBy"])
                    ctx.Check($"'{f}' reported", details.Contains($"\"{f}\""), f, ScenarioContext.Shorten(details));
            });

        Accepted(c, "FLD-017", F, "Optional fields missing (justification, material group, tax code, approvedAt)", Medium,
            r => r.Drop("justification").Drop("approvedAt").Drop("lines[0].materialGroup"),
            "Accepted; SAP header description falls back to the requisition id (the default)",
            async (ctx, doc) => ctx.Equal("SAP description defaults to the id", "REQ-FLD-017", doc["PurReqnDescription"]?.GetValue<string>()));
        Accepted(c, "FLD-018", F, "Unknown extra field (tolerant reader)", Medium, r => r.Set("legacyProjectCode", "PRJ-77").Set("lines[0].colour", "blue"),
            "Ignored; requisition created (unknown fields never reach SAP)");
        Quarantine(c, "FLD-019", F, "Nested object empty (a line with no fields)", High, r => r.Set("lines[0]", new JsonObject()), QuarantineReason.MissingRequiredField, "lines[0].");
        Quarantine(c, "FLD-020", F, "Null item inside the line collection", High, r => { ((JsonArray)r["lines"]!).Add(null); return r; }, QuarantineReason.SchemaInvalid, "lines[1]");

        const string T = Categories.Formats;

        Quarantine(c, "TYP-001", T, "Number sent as a string (\"2\")", High, r => r.Set("lines[0].quantity", "2"), QuarantineReason.SchemaInvalid, "quantity")
            .Describe("Never coerced: a quantity of \"2\" is a type error, reported with the field.");
        Quarantine(c, "TYP-002", T, "Text where an integer is required (lineNo \"ten\")", High, r => r.Set("lines[0].lineNo", "ten"), QuarantineReason.SchemaInvalid, "lineNo");
        Quarantine(c, "TYP-003", T, "Boolean where a number is required", Medium, r => r.Set("totalAmount", true), QuarantineReason.SchemaInvalid, "totalAmount");
        Quarantine(c, "TYP-004", T, "Negative quantity", High, r => r.Set("lines[0].quantity", -3).Recalculate(), QuarantineReason.InvalidQuantity, "quantity");
        Quarantine(c, "TYP-005", T, "Zero quantity", High, r => r.Set("lines[0].quantity", 0).Set("totalAmount", 100), QuarantineReason.InvalidQuantity, "quantity");
        Quarantine(c, "TYP-006", T, "Decimal quantity for a whole-unit item (1.5 EA)", High, r => r.Set("lines[0].quantity", 1.5m).Recalculate(), QuarantineReason.InvalidQuantity, "whole number");
        Quarantine(c, "TYP-007", T, "Negative price", High, r => r.Set("lines[0].unitPrice", -10m).Set("totalAmount", 10), QuarantineReason.InvalidPrice, "unit price");
        Accepted(c, "TYP-008", T, "Zero price on one line (free item) with a positive total", Medium,
            r => { ((JsonArray)r["lines"]!).Add(Req.Line(20, "MAT-FREE", "Free sample", 1, "EA", 0m)); return r.Recalculate(); },
            "Accepted: a free line is allowed; a zero total is not (see TYP-007/AmountNotPositive)");
        Quarantine(c, "TYP-009", T, "Excessive decimal precision in a price (10.123456)", High, r => r.Set("lines[0].unitPrice", 10.123456m).Recalculate(), QuarantineReason.InvalidPrice, "decimal places");
        Accepted(c, "TYP-010", T, "Floating-point traps (0.1 + 0.2) total exactly", Medium,
            r =>
            {
                r["lines"] = new JsonArray(Req.Line(10, quantity: 1, price: 0.1m, uom: "EA"), Req.Line(20, quantity: 1, price: 0.2m, uom: "EA"));
                r["totalAmount"] = 0.3m;
                return r;
            },
            "Decimal arithmetic: 0.1 + 0.2 equals the declared 0.3, no false TotalMismatch",
            async (ctx, doc) => ctx.Equal("SAP amount", 0.3m, doc["to_PurchaseReqnItem"]!.AsArray().Sum(i => i!["RequestedQuantity"]!.GetValue<decimal>() * i["PurchaseRequisitionPrice"]!.GetValue<decimal>())));
        Quarantine(c, "TYP-011", T, "Integer overflow (lineNo 3,000,000,000)", Medium, r => r.Set("lines[0].lineNo", 3_000_000_000L), QuarantineReason.SchemaInvalid, "lineNo");
        Accepted(c, "TYP-012", T, "Extremely large total (preserved exactly, no rounding)", Medium,
            r => r.Set("lines[0].quantity", 1).Set("lines[0].unitPrice", 987_654_321.99m).Recalculate(),
            "Created with the exact amount; no float rounding",
            async (ctx, doc) => ctx.Equal("SAP price exact", 987_654_321.99m, doc["to_PurchaseReqnItem"]![0]!["PurchaseRequisitionPrice"]!.GetValue<decimal>()));
        Quarantine(c, "TYP-013", T, "Invalid currency code (lowercase 'usd')", High, r => r.Set("currency", "usd"), QuarantineReason.UnknownCurrency, "Currency");
        Quarantine(c, "TYP-014", T, "Invalid date (2027-13-45)", High, r => r.Set("neededBy", "2027-13-45"), QuarantineReason.SchemaInvalid, "neededBy");
        Quarantine(c, "TYP-015", T, "Locale date format (03/04/2027) refused as ambiguous", High, r => r.Set("neededBy", "03/04/2027"), QuarantineReason.SchemaInvalid, "neededBy");
        Quarantine(c, "TYP-016", T, "Impossible date (February 30)", High, r => r.Set("neededBy", "2027-02-30"), QuarantineReason.SchemaInvalid, "neededBy");
        Quarantine(c, "TYP-017", T, "Delivery date before the request (in the past)", High, r => r.Set("neededBy", Req.Today(-3)), QuarantineReason.NeededByInPast, "neededBy");

        c.Add("TYP-018", T, "Extremely long description: SAP's 40-character limit made explicit", Medium, Functional)
            .Describe("SAP's item short text holds 40 characters. The integration sends the first 40 and records that it did (an event on the staging step), and the source keeps the full text: nothing is cut silently.")
            .Expect("Created; SAP text = first 40 characters; the truncation is recorded on the requisition's timeline")
            .Run(async ctx =>
            {
                var text = string.Concat(Enumerable.Repeat("Heavy-duty replacement brake assembly ", 200));
                await ctx.Lab.PutSourceAsync("REQ-TYP-018", Req.Valid("REQ-TYP-018").Set("lines[0].description", text));
                await ctx.RunSyncAsync();
                await ctx.ExpectCreatedOnceAsync("REQ-TYP-018");
                var doc = (await ctx.Lab.SapDocumentsForAsync("REQ-TYP-018"))[0];
                ctx.Equal("SAP item text", text[..40], doc["to_PurchaseReqnItem"]![0]!["PurchaseRequisitionItemText"]!.GetValue<string>());
                var staged = (await ctx.Lab.EventsAsync("REQ-TYP-018")).FirstOrDefault(e => e.Kind == Procurement.Sync.Store.RunEventKind.Staged);
                ctx.Check("truncation recorded on the timeline", staged?.Detail?.Contains("40-character") == true, "a note about SAP's 40-character text", staged?.Detail);
                ctx.Equal("source keeps the full text", text.Length, (await ctx.Lab.SourceRecordAsync("REQ-TYP-018"))!["lines"]![0]!["description"]!.GetValue<string>().Length);
            });

        Quarantine(c, "TYP-019", T, "Control characters in text (NUL, BEL)", High, r => r.Set("lines[0].description", "Printer\u0000toner\u0007"), QuarantineReason.SchemaInvalid, "control characters");
        Accepted(c, "TYP-020", T, "HTML in text fields is stored as text, never executed", High,
            r => r.Set("lines[0].description", "<script>alert(1)</script>"),
            "Created; SAP holds the literal text (the web UI renders it as text, see the UX tests)",
            async (ctx, doc) => ctx.Equal("SAP text is the literal string", "<script>alert(1)</script>", doc["to_PurchaseReqnItem"]![0]!["PurchaseRequisitionItemText"]!.GetValue<string>()));

        c.Add("TYP-021", T, "SQL injection strings in the id and text", Critical, Negative, Security)
            .Expect("Treated as data: created once, lookups escaped, the integration database intact")
            .Run(async ctx =>
            {
                const string id = "REQ-'; DROP TABLE Requisitions;--";
                await ctx.Lab.PutSourceAsync(id, Req.Valid(id).Set("lines[0].description", "x' OR '1'='1"));
                await ctx.RunSyncAsync();
                await ctx.ExpectCreatedOnceAsync(id);
                await using var db = ctx.Lab.Db();
                ctx.Check("Requisitions table still exists and is readable", db.Requisitions.Any(), "readable", "ok");
                var lookup = ctx.Lab.Capture.Exchanges.FirstOrDefault(e => e.Method == "GET" && e.Url.Contains("ExternalReference"));
                ctx.Check("the OData filter escaped the quote", lookup is not null && Uri.UnescapeDataString(lookup.Url).Contains("REQ-''; DROP"), "quote doubled", lookup?.Url);
            });

        Accepted(c, "TYP-022", T, "JSON injection payload inside a string", High,
            r => r.Set("lines[0].description", "\"}],\"totalAmount\":0,\"x\":{\""),
            "The text stays one string; totals unaffected",
            async (ctx, doc) => ctx.Equal("SAP price unchanged", 1200m, doc["to_PurchaseReqnItem"]![0]!["PurchaseRequisitionPrice"]!.GetValue<decimal>()));

        c.Add("TYP-023", T, "Unexpected enum values (status 'APPROVED ', unit 'ea')", High, Negative)
            .Expect("'APPROVED ' is not 'Approved': skipped, never assumed. Unit 'ea' quarantined UnknownUnitOfMeasure")
            .Run(async ctx =>
            {
                await ctx.Lab.PutSourceAsync("REQ-TYP-023A", Req.Valid("REQ-TYP-023A").Set("status", "APPROVED "));
                await ctx.Lab.PutSourceAsync("REQ-TYP-023B", Req.Valid("REQ-TYP-023B").Set("lines[0].uom", "ea"));
                await ctx.RunSyncAsync();
                await ctx.ExpectSkippedAsync("REQ-TYP-023A");
                await ctx.ExpectQuarantinedAsync("REQ-TYP-023B", QuarantineReason.UnknownUnitOfMeasure, "unit of measure");
            });

        c.Add("TYP-024", T, "Duplicate JSON properties", Critical, Negative, Security)
            .Describe("The source serves {\"quantity\":1, \"quantity\":999}. Which value counts would depend on the parser, so neither is trusted.")
            .Faults("Source page served raw with a duplicated property").Expect("Quarantined SchemaInvalid naming the duplicated field; nothing sent")
            .Run(async ctx =>
            {
                var raw = Req.Valid("REQ-TYP-024").ToJsonString().Replace("\"quantity\":2", "\"quantity\":2,\"quantity\":999");
                var page = $"{{\"items\":[{raw.TrimEnd('}')},\"version\":1,\"updatedAt\":\"{DateTime.UtcNow:O}\"}}],\"nextCursor\":null,\"hasMore\":false}}";
                await ctx.Lab.ScriptSourceAsync(new ScriptedFault { Kind = FaultKind.Respond, Method = "GET", PathContains = "/api/requisitions", StatusCode = 200, Body = page });
                await ctx.RunSyncAsync();
                var q = await ctx.Lab.OpenQuarantineAsync("REQ-TYP-024");
                ctx.Check("quarantined SchemaInvalid for the duplicated property", q.Any(e => e.Reason == QuarantineReason.SchemaInvalid && (e.Details ?? "").Contains("more than once")), "appears more than once", q.FirstOrDefault()?.Details);
                ctx.Equal("SAP documents", 0, (await ctx.Lab.SapStatsAsync()).Total);
            })
            .Without("INV-02");

        Quarantine(c, "TYP-025", T, "Invalid reference code (plant 9999)", High, r => r.Set("lines[0].plant", "9999"), QuarantineReason.UnknownPlant, "plant");

        Accepted(c, "TYP-026", T, "Case-sensitive material codes passed exactly", Medium, r => r.Set("lines[0].materialCode", "mat-100x"),
            "Created; SAP receives 'mat-100x' exactly (codes are never case-folded)",
            async (ctx, doc) => ctx.Equal("SAP material", "mat-100x", doc["to_PurchaseReqnItem"]![0]!["Material"]!.GetValue<string>()));
        Quarantine(c, "TYP-027", T, "Incorrect unit of measure (PCS)", High, r => r.Set("lines[0].uom", "PCS"), QuarantineReason.UnknownUnitOfMeasure, "unit of measure");
    }
}
