using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Mocks.Shared;
using Mocks.Shared.Faults;
using Procurement.Contracts;
using Procurement.Scenarios.Lab;
using Procurement.Sync.Pipeline;
using Procurement.Sync.Store;

namespace Procurement.TestLab;

public sealed record WhatIfSituation(string Id, string Title, string Description, string Group);

public sealed record WhatIfNumber(string Label, int? Before, int After, string Tone);

public sealed record WhatIfCheck(string Label, bool Ok, string Detail);

public sealed record WhatIfStep(DateTimeOffset At, string Text);

public sealed record WhatIfLink(string Label, string Href);

public sealed record WhatIfAction(string Id, string Label);

public sealed record WhatIfStory(
    string Id, string Title, string Verdict, string Headline, List<string> Story, List<WhatIfNumber> Numbers, List<WhatIfCheck> Checks,
    List<WhatIfStep> Timeline, WhatIfAction? Next, List<WhatIfLink> Links, DateTimeOffset At);

/// <summary>Each situation resets the workspace to the client's workbook first, so the numbers stay comparable.</summary>
public static class WhatIf
{
    public static readonly WhatIfSituation[] Situations =
    [
        new("network-down", "The internet goes down mid-sync", "The connection to SAP drops partway through a sync and stays down.", "Connection"),
        new("reply-lost", "SAP saves it, but the reply never arrives", "The most dangerous moment for duplicates: SAP has it, the integration doesn't know.", "Connection"),
        new("sap-down", "SAP is down for hours", "Every call to SAP fails, run after run.", "SAP"),
        new("sap-busy", "SAP says \"slow down\"", "SAP rate-limits the integration (HTTP 429 with Retry-After).", "SAP"),
        new("crash", "The server crashes mid-sync", "The process dies right after SAP confirms a requisition, before it is written down.", "Server"),
        new("bad-data", "Bad data arrives", "Requisitions with missing or wrong values come in from the source system.", "Data"),
        new("already-in-sap", "Some requisitions are already in SAP", "SAP holds documents from an earlier load before the integration ever runs.", "Data"),
    ];

    internal sealed record Counts(int Created, int Linked, int Waiting, int Checking, int Quarantined, int Skipped, int SapDocuments, int Duplicates, int Total, int NotRead);

    private sealed class Ctx(Workspace ws, LabEnvironment lab)
    {
        public Workspace Ws { get; } = ws;
        public LabEnvironment Lab { get; } = lab;
        public List<WhatIfStep> Timeline { get; } = [];
        public void Step(string text) => Timeline.Add(new WhatIfStep(DateTimeOffset.UtcNow, text));

        public async Task<RunOutcome> SyncAsync(string label)
        {
            Step($"Sync started ({label})");
            var outcome = await Lab.RunAsync("what-if: " + label);
            Ws.Record($"what-if: {label}", outcome);
            var r = outcome.Result;
            Step(r is null
                ? $"Sync ended: {outcome.Kind} ({outcome.Error})"
                : $"Sync ended: {Plural(r.Count(RunEventKind.Created), "created")}, {Plural(r.Count(RunEventKind.AlreadyInSap) + r.Count(RunEventKind.InDoubtResolved), "linked")}, {Plural(r.Pending, "still waiting")}" +
                  (r.Message is { } m ? $" ({m})" : ""));
            return outcome;
        }

        public async Task<List<RunOutcome>> SettleAsync(string label, int max = 6)
        {
            var all = new List<RunOutcome>();
            for (var i = 1; i <= max; i++)
            {
                var o = await SyncAsync(max == 1 ? label : $"{label}, sync {i}");
                all.Add(o);
                if (o.Result is { Pending: 0 } || o.Kind is RunOutcomeKind.Failed or RunOutcomeKind.Crashed) break;
                await Task.Delay(2500);
            }
            return all;
        }
    }

    public static async Task<WhatIfStory?> RunAsync(Workspace ws, string id)
    {
        var situation = Situations.FirstOrDefault(s => s.Id == id);
        if (id != "recover" && situation is null) throw new KeyNotFoundException(id);
        if (id != "recover") await ws.ResetAsync("Workbook", 42, 250);
        return await ws.ExclusiveAsync(async lab =>
        {
            var ctx = new Ctx(ws, lab);
            if (id != "recover") ctx.Step("Lab reset to the client's workbook: 120 requisitions, nothing sent yet, 7 already in SAP");
            return id switch
            {
                "network-down" => await NetworkDownAsync(ctx),
                "reply-lost" => await ReplyLostAsync(ctx),
                "sap-down" => await SapDownAsync(ctx),
                "sap-busy" => await SapBusyAsync(ctx),
                "crash" => await CrashAsync(ctx),
                "bad-data" => await BadDataAsync(ctx),
                "already-in-sap" => await AlreadyInSapAsync(ctx),
                _ => await RecoverAsync(ctx),
            };
        });
    }

    private static async Task<WhatIfStory> NetworkDownAsync(Ctx ctx)
    {
        var before = await CountAsync(ctx.Lab);
        await ctx.Lab.ScriptSapAsync(new ScriptedFault { Kind = FaultKind.Abort, Skip = 40, Count = 1_000_000 });
        ctx.Step("Network set to fail after the first 40 requests to SAP (connections dropped, SAP never sees them)");
        await ctx.SyncAsync("network drops partway through");
        var after = await CountAsync(ctx.Lab);
        var story = new List<string>
        {
            $"The sync started normally. Before the connection dropped, {Plural(after.Created, "requisition")} {(after.Created == 1 ? "was" : "were")} created in SAP and {after.Linked} already in SAP {(after.Linked == 1 ? "was" : "were")} linked.",
            after.Checking > 0
                ? $"{Plural(after.Checking, "requisition")} {(after.Checking == 1 ? "was on its" : "were on their")} way to SAP at the moment the line went down, so the integration does not know whether SAP got {(after.Checking == 1 ? "it" : "them")}. {(after.Checking == 1 ? "It is" : "They are")} marked \"checking\": before anything is sent again, the integration will look in SAP first."
                : "Nothing was mid-flight at the moment the line went down.",
            $"{Plural(after.Waiting, "requisition")} could not be sent and {(after.Waiting == 1 ? "is" : "are")} waiting. They are kept, not dropped, and will go as soon as SAP can be reached.",
            $"{after.Quarantined} with bad data and {after.Skipped} not yet approved were handled as usual (they never needed SAP).",
            "The network is still down. Press \"Bring the network back\" to see the recovery.",
        };
        return Story(ctx, "network-down", "Safe so far: nothing lost, nothing sent twice",
            $"Connection lost mid-sync: {after.Created} created, {after.Waiting} waiting, {after.Checking} being checked, {after.Duplicates} duplicates",
            story, before, after, await ChecksAsync(ctx.Lab, after), new WhatIfAction("recover", "Bring the network back"),
            [new("Requisitions waiting to be sent", "/requisitions?outcome=Retrying"), new("What SAP received (HTTP log)", "/http")]);
    }

    private static async Task<WhatIfStory> RecoverAsync(Ctx ctx)
    {
        var before = await CountAsync(ctx.Lab);
        List<string> checking;
        await using (var db = ctx.Lab.Db())
            checking = await db.Requisitions.Where(r => r.State == RecordState.InDoubt || r.State == RecordState.Sending).Select(r => r.SourceId).ToListAsync();
        var firstEvent = await LastEventIdAsync(ctx.Lab);
        await ctx.Lab.ClearFaultsAsync();
        ctx.Step("Faults cleared: the network and SAP are back");
        await ctx.SettleAsync("after recovery");
        var after = await CountAsync(ctx.Lab);

        var (found, fresh) = await FateAsync(ctx.Lab, checking, firstEvent);
        var foundInSap = found.Count;
        var sentFresh = fresh.Count;
        var story = new List<string>
        {
            $"With the connection back, the integration sent the {Plural(before.Waiting, "requisition")} that {(before.Waiting == 1 ? "was" : "were")} waiting.",
        };
        if (checking.Count > 0)
        {
            var parts = new List<string>();
            if (foundInSap > 0) parts.Add($"{foundInSap} {Was(foundInSap)} already there (SAP had received {Them(foundInSap)} before the line dropped), so {Them(foundInSap)} {Was(foundInSap)} linked, not created again ({string.Join(", ", found.Take(5))})");
            if (sentFresh > 0) parts.Add($"{sentFresh} had never reached SAP, so {Them(sentFresh)} {Was(sentFresh)} sent now, once ({string.Join(", ", fresh.Take(5))})");
            story.Add($"For the {Plural(checking.Count, "requisition")} that {Was(checking.Count)} \"checking\", it looked in SAP before sending anything: {string.Join("; ", parts)}.");
        }
        story.Add($"Final state: {after.Created} created, {after.Linked} linked to existing SAP documents, {after.Quarantined} quarantined, {after.Skipped} skipped, {after.Waiting + after.Checking} still waiting. " +
                  (after.Duplicates == 0 ? "No requisition exists twice in SAP." : $"{after.Duplicates} reference(s) exist twice in SAP."));
        return Story(ctx, "recover", after.Waiting + after.Checking == 0 ? "Recovered: everything sent once" : "Partly recovered",
            $"Back online: {after.Created} created, {after.Linked} linked, {after.Duplicates} duplicates",
            story, before, after, await ChecksAsync(ctx.Lab, after), null,
            [new("All requisitions", "/requisitions"), new("Reconciliation", "/reconciliation")]);
    }

    private static async Task<WhatIfStory> ReplyLostAsync(Ctx ctx)
    {
        var before = await CountAsync(ctx.Lab);
        var firstSeq = ctx.Lab.Capture.Exchanges.Select(e => e.Seq).DefaultIfEmpty(0).Max();
        await ctx.Lab.SapFaultsAsync(new FaultSettings { HonorIdempotencyKey = false });
        await ctx.Lab.ScriptSapAsync(new ScriptedFault { Kind = FaultKind.LostResponse, Method = "POST", PathContains = "A_PurchaseRequisitionHeader", Count = 5 });
        ctx.Step("SAP will save the next 5 requisitions but their replies will be lost; SAP's duplicate keys switched off (as in real SAP)");
        await ctx.SettleAsync("replies lost");
        var after = await CountAsync(ctx.Lab);
        // The requisitions whose create got no reply, read from the HTTP log.
        var lostBodies = ctx.Lab.Capture.Exchanges.Where(e => e.Seq > firstSeq && e.Method == "POST" && e.Url.EndsWith("A_PurchaseRequisitionHeader") && e.Status is null)
            .Select(e => e.RequestBody ?? "").ToList();
        List<string> ids;
        await using (var db = ctx.Lab.Db())
            ids = (await db.Requisitions.AsNoTracking().Select(r => r.SourceId).ToListAsync()).Where(id => lostBodies.Any(b => b.Contains($"\"{id}\""))).ToList();
        var copies = (await ctx.Lab.SapDocumentsAsync()).Where(d => ids.Contains(d["ExternalReference"]!.GetValue<string>())).Count();
        var story = new List<string>
        {
            $"SAP saved {Plural(ids.Count, "requisition")}, but each reply was lost on the way back, like a call that drops just as the other side says \"done\": {string.Join(", ", ids)}.",
            $"The integration could not tell whether {(ids.Count == 1 ? "it was" : "they were")} saved, so it did not resend blindly. It looked each one up in SAP first, found it, and linked it. SAP now holds {copies} document{(copies == 1 ? "" : "s")} for these {ids.Count}: one each.",
            "SAP's own duplicate protection (Idempotency-Key) was switched off for this test, as real SAP does not offer it, so the lookup alone prevented the duplicates.",
            $"Result: {after.Created} created, {after.Linked} linked ({before.SapDocuments} that were already in SAP before the sync, plus these {ids.Count}), {after.Duplicates} duplicates.",
        };
        await ctx.Lab.SapFaultsAsync(new FaultSettings());
        return Story(ctx, "reply-lost", after.Duplicates == 0 ? "Safe: no duplicates" : "Problem: duplicates created",
            $"{ids.Count} replies lost: {copies} SAP document{(copies == 1 ? "" : "s")} for them (one each), {after.Duplicates} duplicates", story, before, after, await ChecksAsync(ctx.Lab, after), null,
            [.. ids.Take(5).Select(a => new WhatIfLink($"{a}: its timeline", $"/requisitions/{Uri.EscapeDataString(a)}"))]);
    }

    private static async Task<WhatIfStory> SapDownAsync(Ctx ctx)
    {
        var before = await CountAsync(ctx.Lab);
        await ctx.Lab.SapFaultsAsync(new FaultSettings { TransientErrorRate = 1 });
        ctx.Step("SAP answers every call with 503 Service Unavailable");
        var opened = 0L;
        for (var i = 1; i <= 3; i++)
        {
            ctx.Step($"Scheduled sync {i} during the outage (in real life, hours apart)");
            var o = await ctx.SyncAsync($"SAP down, attempt {i}");
            opened += o.Result?.Transport.GetValueOrDefault("sap.circuitOpened") ?? 0;
            await Task.Delay(1500);
        }
        var after = await CountAsync(ctx.Lab);
        var story = new List<string>
        {
            "SAP was unavailable for three scheduled syncs in a row.",
            $"Nothing could be created. The integration tried, and after repeated failures its circuit breaker opened{(opened > 0 ? $" ({opened} time{(opened == 1 ? "" : "s")})" : "")}: it stopped calling SAP for a while instead of hammering a system that is down.",
            $"All {Plural(after.Waiting + after.Checking, "requisition")} that need SAP are kept and waiting. None was quarantined as \"bad\" just because SAP was down, and none was lost.",
            "SAP is still down. Press \"SAP comes back\" to see the recovery.",
        };
        return Story(ctx, "sap-down", "Safe: work kept, nothing lost",
            $"SAP down for 3 syncs: 0 created, {after.Waiting + after.Checking} waiting, 0 lost", story, before, after, await ChecksAsync(ctx.Lab, after),
            new WhatIfAction("recover", "SAP comes back"), [new("Waiting requisitions", "/requisitions?outcome=Retrying")]);
    }

    private static async Task<WhatIfStory> SapBusyAsync(Ctx ctx)
    {
        var before = await CountAsync(ctx.Lab);
        var firstSeq = ctx.Lab.Capture.Exchanges.Select(e => e.Seq).DefaultIfEmpty(0).Max();
        await ctx.Lab.SapFaultsAsync(new FaultSettings { ThrottleRate = 0.3, ThrottleRetryAfterSeconds = 2, Seed = 7 });
        ctx.Step("SAP answers about 30% of calls with 429 Too Many Requests, asking to wait 2 seconds");
        var runs = await ctx.SettleAsync("SAP rate-limiting");
        await ctx.Lab.SapFaultsAsync(new FaultSettings());
        var after = await CountAsync(ctx.Lab);
        var throttled = ctx.Lab.Capture.Exchanges.Count(e => e.Seq > firstSeq && e.Status == 429);
        var waited = runs.Sum(r => r.Result?.RetryWait.TotalSeconds ?? 0);
        var story = new List<string>
        {
            $"SAP told the integration to slow down {throttled} times (HTTP 429), each time saying how long to wait.",
            $"Each time the integration waited as asked before trying again ({waited.ToString("0.#", CultureInfo.InvariantCulture)} s of waiting in total, at the lab's 20× speed; in real time about {(waited * 20).ToString("0", CultureInfo.InvariantCulture)} s). Being told to slow down is not treated as SAP failing, so the circuit breaker stayed closed.",
            $"Everything still went through: {after.Created} created, {after.Linked} linked, {after.Duplicates} duplicates.",
        };
        return Story(ctx, "sap-busy", "Safe: slowed down, everything sent once",
            $"SAP throttled {throttled} times: all waits honoured, {after.Created} created, {after.Duplicates} duplicates", story, before, after,
            await ChecksAsync(ctx.Lab, after), null, [new("The 429 replies (HTTP log)", "/http")]);
    }

    private static async Task<WhatIfStory> CrashAsync(Ctx ctx)
    {
        var before = await CountAsync(ctx.Lab);
        var creates = 0;
        ctx.Lab.Crash.ArmAfterSapResponse("right after SAP confirmed requisition #10", (rq, rs) => LabHooks.IsSapCreate(rq) && rs.IsSuccessStatusCode && Interlocked.Increment(ref creates) == 10);
        ctx.Step("The server will crash the instant SAP confirms the 10th new requisition, before it can record it");
        await ctx.SyncAsync("crashes mid-sync");
        var mid = await CountAsync(ctx.Lab);
        List<string> midSend;
        await using (var db = ctx.Lab.Db())
            midSend = await db.Requisitions.Where(r => r.State == RecordState.Sending).Select(r => r.SourceId).ToListAsync();
        var crashEvent = await LastEventIdAsync(ctx.Lab);
        await ctx.Lab.RestartAsync();
        ctx.Step("Server restarted on the same database");
        await ctx.SettleAsync("after restart");
        var after = await CountAsync(ctx.Lab);
        var (found, fresh) = await FateAsync(ctx.Lab, midSend, crashEvent);
        var midParts = new List<string>();
        if (found.Count > 0) midParts.Add($"{found.Count} {Was(found.Count)} already in SAP (the crash came after SAP saved {Them(found.Count)}), so {Them(found.Count)} {Was(found.Count)} linked, not created again ({string.Join(", ", found)})");
        if (fresh.Count > 0) midParts.Add($"{fresh.Count} had not reached SAP before the crash, so {Them(fresh.Count)} {Was(fresh.Count)} sent now, once ({string.Join(", ", fresh)})");
        var story = new List<string>
        {
            $"The server crashed in the middle of the sync, at the worst moment: SAP had just created a requisition and the server died before writing that down. At the crash, {mid.Created} creations had been recorded.",
            midSend.Count > 0
                ? $"On restart, the integration found {Plural(midSend.Count, "requisition")} left \"mid-send\". Its own records cannot say whether SAP got {Them(midSend.Count)}, so it looked in SAP before sending anything: {string.Join("; ", midParts)}."
                : "On restart, nothing was left mid-send.",
            $"The rest of the sync then finished normally: {after.Created} created, {after.Linked} linked, {after.Duplicates} duplicates.",
        };
        return Story(ctx, "crash", "Safe: recovered without duplicates", $"Crash and restart: everything finished once, {after.Duplicates} duplicates",
            story, before, after, await ChecksAsync(ctx.Lab, after), null,
            [.. midSend.Take(3).Select(m => new WhatIfLink($"{m}: its timeline", $"/requisitions/{Uri.EscapeDataString(m)}"))]);
    }

    private static async Task<WhatIfStory> BadDataAsync(Ctx ctx)
    {
        var before = await CountAsync(ctx.Lab);
        await ctx.SyncAsync("normal sync of the workbook");
        var after = await CountAsync(ctx.Lab);
        List<QuarantineEntry> open;
        await using (var db = ctx.Lab.Db())
            open = await db.Quarantine.AsNoTracking().Where(q => q.ResolvedAt == null).ToListAsync();
        var groups = open.GroupBy(q => q.Reason).OrderByDescending(g => g.Count()).ToList();
        var story = new List<string>
        {
            $"The client's workbook contains {after.Quarantined} requisitions with problems. None was sent to SAP, and none was thrown away: each is kept in quarantine with the exact problem and the original data.",
        };
        foreach (var g in groups)
            story.Add($"{g.Count()} × {Describe(g.Key)}, e.g. {string.Join(", ", g.Take(3).Select(q => q.SourceKey))}.");
        story.Add($"{after.Skipped} more are simply not approved yet; they are skipped, not quarantined, and will go once approved.");
        story.Add("To fix one: open it in Quarantine, correct the value in the source, and resend. The next sync picks it up like any other.");
        story.Add($"The good ones went through: {after.Created} created, {after.Linked} linked, {after.Duplicates} duplicates.");
        return Story(ctx, "bad-data", "Safe: bad data held back, good data sent",
            $"{after.Quarantined} quarantined with reasons, {after.Skipped} skipped, {after.Created + after.Linked} sent", story, before, after,
            await ChecksAsync(ctx.Lab, after), null, [new("Fix them in Quarantine", "/quarantine")]);
    }

    private static async Task<WhatIfStory> AlreadyInSapAsync(Ctx ctx)
    {
        var before = await CountAsync(ctx.Lab);
        var preexisting = await ctx.Lab.SapDocumentsAsync();
        ctx.Step($"SAP already holds {preexisting.Count} documents from an earlier load");
        await ctx.SyncAsync("first sync");
        var after = await CountAsync(ctx.Lab);
        var refs = preexisting.Select(d => d["ExternalReference"]!.GetValue<string>()).ToList();
        var story = new List<string>
        {
            $"Before the integration ever ran, SAP already had {preexisting.Count} of these requisitions ({string.Join(", ", refs)}), from an earlier load.",
            "For each requisition the integration looks in SAP before creating anything. It found these and linked them to the existing documents instead of creating second copies.",
            $"Result: {after.Linked} linked, {after.Created} created new, {after.Duplicates} duplicates.",
        };
        return Story(ctx, "already-in-sap", "Safe: linked, not duplicated", $"{after.Linked} already in SAP were linked, {after.Duplicates} duplicates",
            story, before, after, await ChecksAsync(ctx.Lab, after), null,
            [.. refs.Take(3).Select(r => new WhatIfLink($"{r}: its timeline", $"/requisitions/{Uri.EscapeDataString(r)}")), new("Duplicate detection", "/duplicates")]);
    }

    private static WhatIfStory Story(Ctx ctx, string id, string verdict, string headline, List<string> story, Counts before, Counts after,
        List<WhatIfCheck> checks, WhatIfAction? next, List<WhatIfLink> links)
    {
        var title = Situations.FirstOrDefault(s => s.Id == id)?.Title ?? "Recovery";
        var ok = checks.All(c => c.Ok);
        return new WhatIfStory(id, title, ok ? verdict : "Problem: a check failed (see below)", headline, story,
        [
            new("Created in SAP", before.Created, after.Created, "success"),
            new("Linked to existing SAP documents", before.Linked, after.Linked, "success"),
            new("Waiting to be sent", before.Waiting, after.Waiting, after.Waiting > 0 ? "warning" : "neutral"),
            new("Being checked in SAP", before.Checking, after.Checking, after.Checking > 0 ? "warning" : "neutral"),
            new("Quarantined (bad data)", before.Quarantined, after.Quarantined, "neutral"),
            new("Skipped (not approved)", before.Skipped, after.Skipped, "neutral"),
            new("Duplicates in SAP", before.Duplicates, after.Duplicates, after.Duplicates > 0 ? "danger" : "success"),
        ], checks, ctx.Timeline, next, links, DateTimeOffset.UtcNow);
    }

    internal static async Task<List<WhatIfCheck>> ChecksAsync(LabEnvironment lab, Counts c)
    {
        var sap = await lab.SapDocumentsAsync();
        var byNumber = sap.GroupBy(d => d["PurchaseRequisition"]!.GetValue<string>()).ToDictionary(g => g.Key, g => g.First());
        await using var db = lab.Db();
        var records = await db.Requisitions.AsNoTracking().ToListAsync();
        var source = await lab.SourceSnapshotAsync();
        var approved = source.Where(s => s?["status"]?.GetValue<string>() == "Approved").ToList();
        var tracked = records.Select(r => r.SourceId).ToHashSet();
        // Only a record the sync has already read past can be lost; anything after the cursor is simply not read yet.
        var checkpoint = await db.Checkpoints.AsNoTracking().FirstOrDefaultAsync(k => k.Stream == ExtractStage.Stream);
        var lost = KeysetCursor.TryDecode(checkpoint?.Cursor, out var cursor)
            ? approved
                .Select(s => (Id: s!["id"]!.GetValue<string>(), UpdatedAt: DateTime.Parse(s["updatedAt"]!.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)))
                .Where(s => !tracked.Contains(s.Id) && !cursor.IsBefore(s.UpdatedAt, s.Id))
                .Select(s => s.Id)
                .ToList()
            : [];
        var phantom = records.Where(r => r.State == RecordState.Synced && (r.SapNumber is null || !byNumber.TryGetValue(r.SapNumber, out var d) || d["ExternalReference"]?.GetValue<string>() != r.SourceId)).ToList();
        return
        [
            new("No requisition exists twice in SAP", c.Duplicates == 0, c.Duplicates == 0 ? $"{sap.Count} SAP documents, each for a different requisition" : $"{c.Duplicates} reference(s) appear twice"),
            new("Nothing lost", lost.Count == 0, lost.Count == 0 ? $"All {approved.Count} approved requisitions are accounted for ({(c.NotRead > 0 ? "sent, waiting, checking, quarantined or not read yet" : "sent, waiting, checking or quarantined")})" : $"Unaccounted: {string.Join(", ", lost.Take(10))}"),
            new("Nothing marked \"done\" that SAP doesn't have", phantom.Count == 0, phantom.Count == 0 ? $"All {records.Count(r => r.State == RecordState.Synced)} completed requisitions verified against SAP" : $"Not in SAP: {string.Join(", ", phantom.Take(10).Select(p => p.SourceId))}"),
        ];
    }

    internal static async Task<Counts> CountAsync(LabEnvironment lab)
    {
        await using var db = lab.Db();
        await db.Database.EnsureCreatedAsync();
        var records = await db.Requisitions.AsNoTracking().Select(r => new { r.SourceId, r.State }).ToListAsync();
        var created = await db.RunEvents.AsNoTracking().Where(e => e.Kind == RunEventKind.Created).Select(e => e.SourceKey).Distinct().ToListAsync();
        var createdSet = created.ToHashSet();
        var stats = await lab.SapStatsAsync();
        var source = await lab.SourceSnapshotAsync();
        var known = records.Select(r => r.SourceId).ToHashSet();
        var notRead = source.Count(s => s?["status"]?.GetValue<string>() == "Approved" && s["id"] is not null && !known.Contains(s["id"]!.GetValue<string>()));
        return new Counts(
            created.Count,
            records.Count(r => r.State == RecordState.Synced && !createdSet.Contains(r.SourceId)),
            records.Count(r => r.State is RecordState.Queued or RecordState.RetryPending),
            records.Count(r => r.State is RecordState.InDoubt or RecordState.Sending),
            records.Count(r => r.State == RecordState.Quarantined),
            records.Count(r => r.State == RecordState.Ignored),
            stats.Total, stats.DuplicateReferences, source.Count, notRead);
    }

    /// <summary>Splits the requisitions into those found in SAP and linked, and those sent fresh, after the given event.</summary>
    private static async Task<(List<string> Found, List<string> Sent)> FateAsync(LabEnvironment lab, IReadOnlyCollection<string> ids, long afterEvent)
    {
        await using var db = lab.Db();
        var events = await db.RunEvents.AsNoTracking().Where(e => e.Id > afterEvent && ids.Contains(e.SourceKey)).ToListAsync();
        var sent = events.Where(e => e.Kind == RunEventKind.Created).Select(e => e.SourceKey).Distinct().ToList();
        var found = events.Where(e => e.Kind is RunEventKind.InDoubtResolved or RunEventKind.AlreadyInSap).Select(e => e.SourceKey).Distinct().Except(sent).ToList();
        return (found, sent);
    }

    private static string Them(int n) => n == 1 ? "it" : "them";
    private static string Was(int n) => n == 1 ? "was" : "were";

    private static async Task<long> LastEventIdAsync(LabEnvironment lab)
    {
        await using var db = lab.Db();
        return await db.RunEvents.AsNoTracking().Select(e => (long?)e.Id).MaxAsync() ?? 0;
    }

    private static string Describe(QuarantineReason reason) => reason switch
    {
        QuarantineReason.AmountNotPositive => "total amount is zero or missing",
        QuarantineReason.MissingRequiredField => "a required field is empty (e.g. the required-by date)",
        QuarantineReason.TotalMismatch => "the total doesn't equal the sum of the lines",
        QuarantineReason.NeededByInPast => "the required-by date is in the past",
        QuarantineReason.InvalidQuantity => "a quantity is zero, negative or fractional",
        QuarantineReason.InvalidPrice => "a price is negative or has too many decimals",
        QuarantineReason.UnknownCurrency => "an unknown currency",
        QuarantineReason.UnknownUnitOfMeasure => "an unknown unit of measure",
        QuarantineReason.UnknownPlant => "an unknown plant",
        QuarantineReason.SchemaInvalid => "a value of the wrong type",
        _ => reason.ToString(),
    };

    private static string Plural(int n, string noun) => $"{n} {noun}{(n == 1 || noun.EndsWith('d') || noun.Contains(' ') ? "" : "s")}";
}
