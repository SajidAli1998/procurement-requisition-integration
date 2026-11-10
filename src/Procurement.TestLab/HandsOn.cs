using Microsoft.EntityFrameworkCore;
using Procurement.Sync.Store;

namespace Procurement.TestLab;

public static class HandsOn
{
    public static async Task<IResult> DuplicateAsync(Workspace ws, DuplicateRequest req)
    {
        var lab = ws.Lab;
        var record = await lab.SourceRecordAsync(req.Id);
        if (record is null) return Results.Problem(statusCode: 404, title: $"Requisition {req.Id} not found");

        switch (req.How)
        {
            case "resend":
                // The source sends the same requisition again: a new version, identical content.
                await lab.EditSourceAsync(req.Id, new { justification = record["justification"]?.GetValue<string>() });
                return Results.Json(new { message = $"{req.Id} was sent again by the source, unchanged. Run the sync: it must not create a second copy in SAP." });

            case "change":
                var lines = record["lines"]!.DeepClone().AsArray();
                if (lines.Count == 0) return Results.Problem(statusCode: 400, title: $"{req.Id} has no lines to change");
                var first = lines[0]!.AsObject();
                var qty = decimal.Parse(first["quantity"]!.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture) + 1;
                first["quantity"] = qty;
                var total = lines.Sum(l => decimal.Parse(l!["quantity"]!.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture)
                                           * decimal.Parse(l["unitPrice"]!.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture));
                await lab.EditSourceAsync(req.Id, new { lines, totalAmount = Math.Round(total, 2) });
                return Results.Json(new { message = $"{req.Id} was sent again with the first line's quantity raised to {qty}. Run the sync: SAP's existing document must be updated, not copied." });

            case "in-sap":
                if ((await lab.SapDocumentsForAsync(req.Id)).Count > 0)
                    return Results.Problem(statusCode: 409, title: $"SAP already has {req.Id}",
                        detail: "Pick a requisition that has not been sent yet, to see the integration find it in SAP instead of creating it.");
                var line = record["lines"]?[0];
                var plant = line?["plant"]?.GetValue<string>() ?? "1100";
                var company = record["companyCode"]?.GetValue<string>() ?? "1000";
                var number = await lab.InsertSapAsync(req.Id, record["version"]?.GetValue<long>() ?? 1,
                    quantity: line?["quantity"] is { } q ? decimal.Parse(q.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture) : 1,
                    price: line?["unitPrice"] is { } p ? decimal.Parse(p.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture) : 1,
                    plant: plant, company: company, currency: record["currency"]?.GetValue<string>() ?? "USD");
                return Results.Json(new { message = $"Someone created {req.Id} in SAP by hand ({number}). Run the sync: the integration must link to it instead of creating another." });

            default:
                return Results.Problem(statusCode: 400, title: "how must be resend, change or in-sap");
        }
    }

    public static async Task<object> ChecksAsync(Workspace ws)
    {
        var lab = ws.Lab;
        var counts = await WhatIf.CountAsync(lab);
        var checks = await WhatIf.ChecksAsync(lab, counts);
        var running = ws.Active is not null;
        var paused = ws.Paused;
        var toGo = counts.Waiting + counts.Checking + counts.NotRead;

        string tone, title, text;
        if (checks.Any(c => !c.Ok))
        {
            tone = "danger";
            title = "Something is wrong";
            text = string.Join(" ", checks.Where(c => !c.Ok).Select(c => $"{c.Label}: {c.Detail}."));
        }
        else if (running && paused)
        {
            (tone, title, text) = ("info", "Paused", "Requests already sent finish; nothing new is sent until you press Resume.");
        }
        else if (running)
        {
            (tone, title, text) = ("info", "Sync running", $"{counts.Created + counts.Linked} in SAP so far, {toGo} to go.");
        }
        else if (counts.Created + counts.Linked + counts.Quarantined + counts.Waiting + counts.Checking + counts.Skipped == 0)
        {
            (tone, title, text) = ("neutral", "Nothing sent yet", "Start a sync to begin.");
        }
        else if (toGo > 0)
        {
            var hint = ws.Network.Sap == "down" ? "The connection to SAP is switched off: turn it back on, then run the sync again."
                : ws.Network.Source == "down" ? "The connection to the source system is switched off: turn it back on, then run the sync again."
                : "Run the sync again to finish them.";
            (tone, title, text) = ("warning", "Not finished yet", $"{toGo} requisition{(toGo == 1 ? "" : "s")} still to send, none lost. {hint}");
        }
        else
        {
            (tone, title, text) = ("success", "It worked",
                "Every approved requisition is in SAP exactly once or held back with a reason. Nothing was lost and nothing was sent twice.");
        }

        await using var db = lab.Db();
        var choices = await db.Requisitions.AsNoTracking().Select(r => new { r.SourceId, State = r.State.ToString(), r.SapNumber }).ToListAsync();
        var source = await lab.SourceSnapshotAsync();
        var byId = choices.ToDictionary(c => c.SourceId);
        var requisitions = source
            .Where(s => s?["status"]?.GetValue<string>() == "Approved" && s["id"] is not null)
            .Select(s => s!["id"]!.GetValue<string>())
            .Select(id => new { id, state = byId.TryGetValue(id, out var c) ? (c.State == nameof(RecordState.Synced) ? "In SAP" : c.State == nameof(RecordState.Quarantined) ? "Quarantined" : "Not sent yet") : "Not sent yet" })
            .ToList();

        return new
        {
            verdict = new { tone, title, text },
            running, paused, network = ws.Network,
            counts = new
            {
                inSap = counts.Created + counts.Linked, counts.Created, counts.Linked, counts.Waiting, counts.Checking, notRead = counts.NotRead,
                counts.Quarantined, counts.Skipped, counts.Duplicates, toGo, total = counts.Total,
            },
            checks,
            requisitions,
        };
    }
}
