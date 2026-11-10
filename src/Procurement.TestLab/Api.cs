using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Mocks.Shared.Faults;
using Procurement.Scenarios.Catalogue;
using Procurement.Scenarios.Engine;
using Procurement.Scenarios.Generator;
using Procurement.Scenarios.Import;
using Procurement.Scenarios.Lab;
using Procurement.SyntheticData;

namespace Procurement.TestLab;

public sealed record RunScenariosRequest(string[]? Ids, string? Category, Mode? Mode, Priority? Priority, string? Search, bool All, string? Name, int? Parallelism, int? Seed = null);
public sealed record ResetRequest(string? Dataset, int? Seed, int? Count);
public sealed record ReplayRequest(string[] Ids);
public sealed record ScheduleRequest(bool Enabled, int? Seconds);
public sealed record DbFaultRequest(string Kind, string Match, int? Times);
public sealed record CrashRequest(string Point);
public sealed record AssistantCall(string User, string[]? Scopes, string Method, string Path, Dictionary<string, string>? Headers, string? Body, bool Anonymous);
public sealed record SapInsert(string Reference, long? Version, string? Number, string? Lifecycle);
public sealed record ResolveRequest(string Note);
public sealed record DuplicateRequest(string Id, string How);

/// <summary>Reads are open on localhost; anything that changes lab state needs the X-Lab-Key header.</summary>
public static class Api
{
    public const string KeyHeader = "X-Lab-Key";

    private static readonly ConcurrentDictionary<string, BatchProgress> Batches = new();

    public sealed class BatchProgress
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required int Total { get; init; }
        public Tally Tally { get; set; } = new(0, 0, 0, 0, 0, 0, 0);
        public string State { get; set; } = "Running";
        public List<RunnerEvent> Events { get; } = [];
        public CancellationTokenSource Cancel { get; } = new();
        public Lock Gate { get; } = new();
    }

    public static void Map(WebApplication app, string labKey)
    {
        // Checked before the endpoint runs (and before its body is bound), so a malformed request without the key
        // gets 401, never a binding error. Only these POSTs are read-only computations.
        string[] readOnlyPosts = ["/api/workspace/validate", "/api/generator/preview"];
        app.Use(async (http, next) =>
        {
            var method = http.Request.Method;
            var path = http.Request.Path.Value ?? "";
            var mutating = method is "POST" or "PUT" or "DELETE" or "PATCH";
            if (mutating && path.StartsWith("/api/", StringComparison.Ordinal) && !readOnlyPosts.Contains(path)
                && !CryptoEquals(http.Request.Headers[KeyHeader].ToString(), labKey))
            {
                await Results.Problem(statusCode: 401, title: "Lab key required", detail: $"Send the {KeyHeader} header; lab actions are refused without it.").ExecuteAsync(http);
                return;
            }
            await next(http);
        });
        var api = app.MapGroup("/api");
        var write = api.MapGroup("");

        api.MapGet("/health", () => Results.Ok(new { status = "Healthy", isolated = true, environment = RunEnvironment.Current() }));

        api.MapGet("/catalogue", () => Results.Json(new
        {
            categories = Categories.Ordered,
            modes = Enum.GetNames<Mode>(),
            invariants = Invariants.Names.Select(kv => new { id = kv.Key, name = kv.Value }),
            scenarios = ScenarioCatalogue.All.Select(s => new
            {
                s.Id, s.Category, s.Title, s.Description, s.Priority, s.Severity, s.Modes, s.Preconditions, s.Fixture, s.Faults, s.Steps,
                s.Expected, invariants = s.Invariants.Order(), s.BlockedReason, s.CoveredBy, s.Implemented, s.TimeoutSeconds,
            }),
        }, ScenarioRunner.Json));

        api.MapGet("/batches", () => Results.Json(new
        {
            active = Batches.Values.Where(b => b.State == "Running").Select(b => new { b.Id, b.Name, b.Total, b.Tally }),
            history = Cli.Store().History(),
        }, ScenarioRunner.Json));

        api.MapGet("/batches/{id}", async (string id, bool? summary) =>
            await Cli.Store().LoadAsync(id, summary == true) is { } batch
                ? Results.Json(summary == true ? ResultStore.Summary(batch) : batch, ScenarioRunner.Json) : Results.NotFound());
        api.MapGet("/scenarios/latest", () => Results.Json(Cli.Store().LatestResults(), ScenarioRunner.Json))
            .WithSummary("The latest result of every scenario across all saved batches (small: no evidence).");

        api.MapGet("/batches/{id}/results/{scenario}", async (string id, string scenario) =>
            (await Cli.Store().LoadAsync(id))?.Results.FirstOrDefault(r => r.Id == scenario) is { } r ? Results.Json(r, ScenarioRunner.Json) : Results.NotFound());

        api.MapGet("/batches/{id}/export", async (string id, string? format) =>
        {
            var batch = await Cli.Store().LoadAsync(id);
            if (batch is null) return Results.NotFound();
            return (format ?? "json").ToLowerInvariant() switch
            {
                "csv" => Results.File(Encoding.UTF8.GetBytes(Exports.Csv(batch)), "text/csv", $"{batch.Id}.csv"),
                "md" => Results.File(Encoding.UTF8.GetBytes(Exports.Markdown(batch)), "text/markdown", $"{batch.Id}.md"),
                "html" => Results.File(Encoding.UTF8.GetBytes(Exports.Html(batch)), "text/html", $"{batch.Id}.html"),
                _ => Results.File(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(batch, ScenarioRunner.Json)), "application/json", $"{batch.Id}.json"),
            };
        });

        write.MapPost("/batches", (RunScenariosRequest req) =>
        {
            var selected = req.All ? ScenarioCatalogue.All : ScenarioCatalogue.Select(req.Ids, req.Category, req.Mode, req.Priority, req.Search);
            if (selected.Count == 0) return Results.Problem(statusCode: 400, title: "No scenario matches the selection");
            var name = req.Name ?? (req.All ? "Full regression" : req.Mode is { } m ? $"{m} mode" : req.Category ?? string.Join(' ', req.Ids ?? []));
            var progress = new BatchProgress { Id = "pending-" + Guid.NewGuid().ToString("N")[..8], Name = name, Total = selected.Count };
            Batches[progress.Id] = progress;
            _ = Task.Run(async () =>
            {
                try
                {
                    var batch = await ScenarioRunner.RunAsync(selected, new RunRequest { Name = name, Parallelism = Math.Min(req.Parallelism ?? 4, MaxParallelism), Seed = req.Seed, FullEvidence = MaxParallelism > 1 }, e =>
                    {
                        lock (progress.Gate)
                        {
                            if (e.Tally is not null) progress.Tally = e.Tally;
                            if (e.Type != "http") progress.Events.Add(e with { BatchId = progress.Id });
                        }
                    }, progress.Cancel.Token);
                    var saved = await Cli.Store().SaveAsync(batch);
                    lock (progress.Gate)
                    {
                        progress.State = "Finished";
                        progress.Events.Add(new RunnerEvent(progress.Id, "saved", null, batch.Id, Tally: batch.Tally));
                    }
                }
                catch (OperationCanceledException)
                {
                    // The runner saves partial results itself when cancelled; this only covers a cancel before it started.
                    lock (progress.Gate) progress.State = "Cancelled";
                }
                catch (Exception ex)
                {
                    lock (progress.Gate)
                    {
                        progress.State = "Failed";
                        progress.Events.Add(new RunnerEvent(progress.Id, "error", null, ex.GetBaseException().Message));
                    }
                }
            });
            return Results.Accepted($"/api/batches/live/{progress.Id}", new { progress.Id, progress.Name, progress.Total });
        });

        write.MapDelete("/batches/live/{id}", (string id) =>
        {
            if (!Batches.TryGetValue(id, out var p)) return Results.NotFound();
            p.Cancel.Cancel();
            return Results.NoContent();
        });

        // Replays what happened so far, then streams.
        api.MapGet("/batches/live/{id}", async (string id, HttpContext http) =>
        {
            if (!Batches.TryGetValue(id, out var p)) return Results.NotFound();
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            var sent = 0;
            while (!http.RequestAborted.IsCancellationRequested)
            {
                RunnerEvent[] pending;
                string state;
                lock (p.Gate)
                {
                    pending = p.Events.Skip(sent).ToArray();
                    state = p.State;
                }
                foreach (var e in pending) await Sse(http, e.Type, e);
                sent += pending.Length;
                if (state != "Running" && pending.Length == 0)
                {
                    await Sse(http, "end", new { state });
                    break;
                }
                await Task.Delay(200, http.RequestAborted).ContinueWith(_ => { });
            }
            return Results.Empty;
        });

        api.MapGet("/workspace", async (Workspace ws) =>
        {
            var stats = await ws.Lab.SapStatsAsync();
            var faults = new { sap = await Get(ws.Lab.SapAdmin, "/_admin/faults"), source = await Get(ws.Lab.SourceAdmin, "/_admin/faults") };
            return Results.Json(new
            {
                ws.Dataset, seed = ws.Lab.Options.Seed, count = ws.Lab.Options.Count, ws.CreatedAt, scheduled = ws.Scheduled, scheduleSeconds = ws.ScheduleSeconds,
                active = ws.Active, sap = new { total = stats.Total, duplicateReferences = stats.DuplicateReferences },
                faults = new { sap = CamelCase(faults.sap), source = CamelCase(faults.source) },
                crashArmed = ws.Lab.Crash.Armed, crashFiredAt = ws.Lab.Crash.FiredAt, dbFaultsArmed = ws.Lab.DbFaults.Pending, dbFaultsFired = ws.Lab.DbFaults.Fired,
            }, Workspace.Json);
        });

        write.MapPost("/workspace/reset", async (Workspace ws, ResetRequest req) =>
        {
            var dataset = req.Dataset ?? "Workbook";
            if (dataset is not ("Workbook" or "Generated" or "Empty")) return Results.Problem(statusCode: 400, title: "dataset must be Workbook, Generated or Empty");
            await ws.ResetAsync(dataset, req.Seed ?? 42, Math.Clamp(req.Count ?? 250, 1, 20000));
            return Results.NoContent();
        });

        api.MapGet("/workspace/dashboard", async (Workspace ws) => Results.Json(await Dashboard.BuildAsync(ws), Workspace.Json));
        api.MapGet("/workspace/requisitions", async (Workspace ws) => Results.Json(await ws.RequisitionRowsAsync(), Workspace.Json));
        api.MapGet("/workspace/requisitions/{id}", async (Workspace ws, string id) =>
            await ws.RequisitionDetailAsync(id) is { } d ? Results.Json(d, Workspace.Json) : Results.NotFound());

        write.MapPut("/workspace/requisitions/{id}", async (Workspace ws, string id, HttpRequest request, long? version) =>
        {
            var (body, error) = await ReadObjectAsync(request);
            return error ?? Results.Json(await ws.Lab.PutSourceAsync(id, body!, version), Workspace.Json);
        });
        write.MapPost("/workspace/requisitions/{id}/edit", async (Workspace ws, string id, HttpRequest request) =>
        {
            var (patch, error) = await ReadObjectAsync(request);
            if (error is not null) return error;
            var response = await ws.Lab.SourceAdmin.PostAsJsonAsync($"/_admin/requisitions/{Uri.EscapeDataString(id)}/edit", patch);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return Results.Problem(statusCode: 404, title: $"Requisition {id} not found");
            response.EnsureSuccessStatusCode();
            return Results.Json(await ws.Lab.SourceRecordAsync(id), Workspace.Json);
        });
        write.MapDelete("/workspace/requisitions/{id}", async (Workspace ws, string id) =>
        {
            var response = await ws.Lab.SourceAdmin.DeleteAsync($"/_admin/requisitions/{Uri.EscapeDataString(id)}");
            return response.StatusCode == System.Net.HttpStatusCode.NotFound ? Results.Problem(statusCode: 404, title: $"Requisition {id} not found") : Results.NoContent();
        });
        api.MapPost("/workspace/validate", async (Workspace ws, HttpRequest request) =>
        {
            var (record, error) = await ReadObjectAsync(request);
            return error ?? Results.Json(ws.Validate(record!), Workspace.Json);
        });

        api.MapGet("/workspace/runs", (Workspace ws) => Results.Json(ws.Runs.Reverse(), Workspace.Json));
        write.MapPost("/workspace/sync", (Workspace ws) => ws.StartSync() is { } run ? Results.Accepted(null, run) : Busy());
        write.MapPost("/workspace/replay", (Workspace ws, ReplayRequest req) =>
            req.Ids is not { Length: > 0 } ? Results.Problem(statusCode: 400, title: "ids required") : ws.StartReplay(req.Ids) is { } run ? Results.Accepted(null, run) : Busy());
        write.MapPost("/workspace/stop", (Workspace ws) => Results.Json(new { stopped = ws.Stop() }));
        write.MapPost("/workspace/schedule", (Workspace ws, ScheduleRequest req) =>
        {
            if (req.Enabled) ws.StartSchedule(req.Seconds ?? 10);
            else ws.StopSchedule();
            return Results.Json(new { enabled = ws.Scheduled, seconds = ws.ScheduleSeconds });
        });
        write.MapPost("/workspace/restart", async (Workspace ws) =>
        {
            await ws.RestartAsync();
            return Results.NoContent();
        });
        write.MapPost("/workspace/crash", (Workspace ws, CrashRequest req) =>
        {
            switch (req.Point)
            {
                case "after-sap-create":
                    ws.Lab.Crash.ArmAfterSapResponse("after SAP created a requisition, before the mapping was saved",
                        (rq, rs) => LabHooks.IsSapCreate(rq) && rs.IsSuccessStatusCode);
                    break;
                case "mid-page":
                    ws.Lab.DbFaults.Add("crash while saving a page checkpoint", DbFaults.SavesCheckpoint, DbFaultKind.Crash);
                    break;
                default:
                    return Results.Problem(statusCode: 400, title: "point must be after-sap-create or mid-page");
            }
            return Results.Json(new { armed = req.Point });
        });
        write.MapPost("/workspace/reconcile", async (Workspace ws) => Results.Json(await ws.Lab.ReconcileAsync(), Workspace.Json));

        write.MapPut("/workspace/faults/{target}", async (Workspace ws, string target, FaultSettings settings) =>
            await Forward(ws, target, c => c.PutAsJsonAsync("/_admin/faults", settings)));
        write.MapPost("/workspace/faults/{target}/script", async (Workspace ws, string target, ScriptedFault[] script) =>
            await Forward(ws, target, c => c.PostAsJsonAsync("/_admin/faults/script", script)));
        write.MapDelete("/workspace/faults", async (Workspace ws) =>
        {
            await ws.Lab.ClearFaultsAsync();
            return Results.NoContent();
        });
        write.MapPost("/workspace/faults/db", (Workspace ws, DbFaultRequest req) =>
        {
            Func<DbContext, bool>? match = req.Match switch
            {
                "mapping" => DbFaults.SavesMapping, "checkpoint" => DbFaults.SavesCheckpoint, "sending" => DbFaults.SavesSending, _ => null,
            };
            if (match is null || !Enum.TryParse<DbFaultKind>(req.Kind, true, out var kind)) return Results.Problem(statusCode: 400, title: "match: mapping|checkpoint|sending; kind: SaveFails|DatabaseLocked|Crash");
            ws.Lab.DbFaults.Add($"{req.Kind} on {req.Match} save", match, kind, req.Times ?? 1);
            return Results.Json(new { armed = $"{req.Kind} on {req.Match}" });
        });

        api.MapGet("/workspace/sap/documents", async (Workspace ws) => Results.Json(await ws.Lab.SapDocumentsAsync(), Workspace.Json));
        write.MapPost("/workspace/sap/documents", async (Workspace ws, SapInsert req) =>
            Results.Json(new { number = await ws.Lab.InsertSapAsync(req.Reference, req.Version ?? 1, req.Number, req.Lifecycle) }));
        write.MapPost("/workspace/sap/advance", async (Workspace ws) =>
        {
            await ws.Lab.AdvanceSapAsync();
            return Results.NoContent();
        });
        write.MapPost("/workspace/sap/{number}/lifecycle/{lifecycle}", async (Workspace ws, string number, string lifecycle) =>
        {
            await ws.Lab.SetSapLifecycleAsync(number, lifecycle);
            return Results.NoContent();
        });
        write.MapDelete("/workspace/sap/{number}", async (Workspace ws, string number) =>
        {
            await ws.Lab.DeleteSapAsync(number);
            return Results.NoContent();
        });
        write.MapPost("/workspace/sap/evict-keys", async (Workspace ws) =>
        {
            await ws.Lab.EvictSapIdempotencyAsync();
            return Results.NoContent();
        });

        api.MapGet("/workspace/quarantine", async (Workspace ws) => Results.Json(await QuarantineView.BuildAsync(ws), Workspace.Json));
        write.MapPost("/workspace/quarantine/{id}/resolve", async (Workspace ws, string id, ResolveRequest req) =>
        {
            await using var scope = ws.Lab.Worker.Services.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<Procurement.Sync.Pipeline.QuarantineService>();
            return await service.ResolveAsync(id, req.Note, CancellationToken.None) ? Results.NoContent() : Results.NotFound();
        });

        api.MapGet("/workspace/reconciliation", async (Workspace ws) => Results.Json(await Reconciliation.BuildAsync(ws), Workspace.Json));
        api.MapGet("/workspace/reports/{format}", (Workspace ws, string format) =>
        {
            var ext = format.ToLowerInvariant() switch { "json" => "json", "md" or "markdown" => "md", "html" => "html", "csv" => "csv", _ => null };
            if (ext is null) return Results.Problem(statusCode: 400, title: "format: json, md, html or csv");
            var path = Path.Combine(ws.Lab.ReportDirectory, "latest." + ext);
            return File.Exists(path)
                ? Results.File(File.ReadAllBytes(path), ext switch { "json" => "application/json", "md" => "text/markdown", "html" => "text/html", _ => "text/csv" }, "reconciliation-latest." + ext)
                : Results.Problem(statusCode: 404, title: "No report yet: run a sync first");
        });

        api.MapGet("/workspace/assistant/users", () =>
            Results.Json(EmployeeDirectory.Load().Employees.Select(e => new { e.Id, e.Name, e.Department, e.CanRaise, e.ProcurementDelegate }).Append(
                new { Id = "E9999", Name = "Unknown employee (not in the directory)", Department = "-", CanRaise = true, ProcurementDelegate = false })));
        write.MapPost("/workspace/assistant/call", async (Workspace ws, AssistantCall call) =>
        {
            if (!call.Path.StartsWith("/v1/", StringComparison.Ordinal)) return Results.Problem(statusCode: 400, title: "Only /v1/ assistant endpoints can be called");
            using var client = call.Anonymous ? ws.Lab.Assistant.Anonymous() : ws.Lab.Assistant.ClientFor(call.User, call.Scopes);
            using var request = new HttpRequestMessage(new HttpMethod(call.Method), call.Path);
            if (call.Body is not null && call.Method is not ("GET" or "DELETE")) request.Content = new StringContent(call.Body, Encoding.UTF8, "application/json");
            foreach (var (k, v) in call.Headers ?? []) request.Headers.TryAddWithoutValidation(k, v);
            var started = DateTime.UtcNow;
            using var response = await client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            return Results.Json(new
            {
                status = (int)response.StatusCode,
                durationMs = Math.Round((DateTime.UtcNow - started).TotalMilliseconds, 1),
                correlationId = request.Headers.TryGetValues("X-Correlation-Id", out var c) ? c.FirstOrDefault() : null,
                headers = response.Headers.Concat(response.Content.Headers).ToDictionary(h => h.Key, h => string.Join(", ", h.Value)),
                body = text,
            });
        });

        api.MapGet("/workspace/live", async (Workspace ws, HttpContext http) =>
        {
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            var channel = ws.Subscribe();
            try
            {
                await Sse(http, "hello", new { ws.Dataset });
                await foreach (var e in channel.Events.Reader.ReadAllAsync(http.RequestAborted)) await Sse(http, e.Type, e);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                ws.Unsubscribe(channel);
            }
            return Results.Empty;
        });
        api.MapGet("/workspace/http", (Workspace ws, int? take) => Results.Json(ws.Lab.Capture.Exchanges.OrderByDescending(e => e.Seq).Take(Math.Clamp(take ?? 300, 1, 5000)), Workspace.Json));

        api.MapPost("/generator/preview", (GeneratorOptions o) =>
        {
            if (o.Count is < 1 or > 20000) return Results.Problem(statusCode: 400, title: "count must be 1 to 20000");
            var set = SyntheticGenerator.Generate(o);
            return Results.Json(new
            {
                records = set.Records.Select(r => r.Record),
                manifest = set.Records.Select(r => new { r.Id, r.Traits, r.Expected }),
                sapPreloads = set.SapPreloads.Count, sapFaults = set.SapFaults.Count, summary = set.Summary, csv = SyntheticGenerator.Csv(set),
            }, Workspace.Json);
        });
        write.MapPost("/generator/apply", async (Workspace ws, GeneratorOptions o) =>
        {
            if (o.Count is < 1 or > 20000) return Results.Problem(statusCode: 400, title: "count must be 1 to 20000");
            var set = SyntheticGenerator.Generate(o);
            await ws.ResetAsync("Empty", o.Seed, o.Count);
            foreach (var r in set.Records) await ws.Lab.PutSourceAsync(r.Id, r.Record);
            foreach (var p in set.SapPreloads) await ws.Lab.InsertSapAsync(p.Reference, p.Version, quantity: p.Quantity, price: p.Price, plant: p.Plant, company: p.Company, currency: p.Currency);
            if (set.SapFaults.Count > 0) await ws.Lab.ScriptSapAsync([.. set.SapFaults]);
            return Results.Json(new { loaded = set.Records.Count, sapPreloaded = set.SapPreloads.Count, faultsScripted = set.SapFaults.Count, summary = set.Summary });
        });

        write.MapPost("/workspace/import", async (Workspace ws, HttpRequest request, bool? apply) =>
        {
            if (!request.HasFormContentType) return Results.Problem(statusCode: 400, title: "Send multipart/form-data with a 'file' field");
            var form = await request.ReadFormAsync();
            var file = form.Files["file"] ?? (form.Files.Count > 0 ? form.Files[0] : null);
            if (file is null) return Results.Problem(statusCode: 400, title: "No file in the upload");
            if (file.Length > RequisitionImporter.MaxBytes) return Results.Problem(statusCode: 413, title: $"Files over {RequisitionImporter.MaxBytes / 1024 / 1024} MB are refused");
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var result = RequisitionImporter.Import(ms.ToArray(), file.FileName, file.ContentType);
            var applied = false;
            if (apply == true && result.Ok)
            {
                foreach (var record in result.Records)
                {
                    var version = record["importVersion"]?.GetValue<long>();
                    var copy = record.DeepClone().AsObject();
                    copy.Remove("importVersion");
                    await ws.Lab.PutSourceAsync(copy["id"]!.GetValue<string>(), copy, version);
                }
                applied = true;
            }
            return Results.Json(new
            {
                result.FileName, result.Format, result.Sheets, requisitions = result.Records.Count, result.Lines, result.RowsRead, result.RowsSkipped,
                result.Errors, result.Warnings, ok = result.Ok, applied, records = result.Records.Take(500),
            }, Workspace.Json);
        }).DisableAntiforgery();

        write.MapPost("/workspace/pause", (Workspace ws) =>
        {
            ws.Pause();
            ws.Publish("paused", null);
            return Results.Json(new { paused = true, running = ws.Active is not null });
        });
        write.MapPost("/workspace/resume", (Workspace ws) =>
        {
            ws.Resume();
            ws.Publish("resumed", null);
            return Results.Json(new { paused = false });
        });
        write.MapPost("/workspace/network", async (Workspace ws, NetworkState state) =>
        {
            string[] allowed = ["up", "down", "unreliable"];
            if (!allowed.Contains(state.Sap) || !allowed.Contains(state.Source))
                return Results.Problem(statusCode: 400, title: "sap and source must be up, down or unreliable");
            await ws.SetNetworkAsync(state);
            return Results.Json(ws.Network, Workspace.Json);
        });
        write.MapPost("/workspace/duplicate", async (Workspace ws, DuplicateRequest req) => await HandsOn.DuplicateAsync(ws, req));
        api.MapGet("/workspace/checks", async (Workspace ws) => Results.Json(await HandsOn.ChecksAsync(ws), Workspace.Json));

        api.MapGet("/whatif", () => Results.Json(WhatIf.Situations, Workspace.Json));
        write.MapPost("/whatif/{id}", async (Workspace ws, string id) =>
        {
            if (id != "recover" && WhatIf.Situations.All(s => s.Id != id)) return Results.Problem(statusCode: 404, title: $"Unknown situation '{id}'");
            return await WhatIf.RunAsync(ws, id) is { } story ? Results.Json(story, Workspace.Json) : Busy();
        });

        api.MapGet("/baseline", () => Results.Json(Baseline.Expected()));
    }

    // Duplicate property names are refused because which value wins depends on the parser.
    private static async Task<(JsonObject? Body, IResult? Error)> ReadObjectAsync(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body);
        var text = await reader.ReadToEndAsync(request.HttpContext.RequestAborted);
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (null, Results.Problem(statusCode: 400, title: "The body must be a JSON object"));
            var duplicate = FirstDuplicate(doc.RootElement, "$");
            if (duplicate is not null)
                return (null, Results.Problem(statusCode: 400, title: "Duplicate JSON property", detail: $"'{duplicate}' appears more than once. To test how the integration handles this, see scenario TYP-024 (a raw source page)."));
            return (JsonNode.Parse(text)!.AsObject(), null);
        }
        catch (JsonException ex)
        {
            return (null, Results.Problem(statusCode: 400, title: "Malformed JSON", detail: ex.Message));
        }
    }

    private static string? FirstDuplicate(JsonElement e, string path)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in e.EnumerateObject())
            {
                if (!seen.Add(p.Name)) return $"{path}.{p.Name}";
                if (FirstDuplicate(p.Value, $"{path}.{p.Name}") is { } d) return d;
            }
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var item in e.EnumerateArray()) if (FirstDuplicate(item, $"{path}[{i++}]") is { } d) return d;
        }
        return null;
    }

    /// <summary>The SAP mock speaks PascalCase; the API answers in camelCase throughout.</summary>
    private static JsonNode? CamelCase(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.Select(kv => KeyValuePair.Create(kv.Key.Length > 0 ? char.ToLowerInvariant(kv.Key[0]) + kv.Key[1..] : kv.Key, CamelCase(kv.Value)))),
        JsonArray a => new JsonArray(a.Select(CamelCase).ToArray()),
        _ => node?.DeepClone(),
    };

    private static IResult Busy() => Results.Problem(statusCode: 409, title: "A run is already in progress", detail: "One run at a time, as the integration's lease enforces.");

    private static async Task<IResult> Forward(Workspace ws, string target, Func<HttpClient, Task<HttpResponseMessage>> call)
    {
        var client = target switch { "sap" => ws.Lab.SapAdmin, "source" => ws.Lab.SourceAdmin, _ => null };
        if (client is null) return Results.Problem(statusCode: 400, title: "target must be sap or source");
        var response = await call(client);
        return Results.Content(await response.Content.ReadAsStringAsync(), "application/json", Encoding.UTF8, (int)response.StatusCode);
    }

    private static async Task<JsonNode?> Get(HttpClient client, string path) => await client.GetFromJsonAsync<JsonNode>(path);

    // Small hosts (TESTLAB_MAX_PARALLELISM=1) run one lab at a time and keep a trimmed HTTP log per scenario.
    private static readonly int MaxParallelism =
        int.TryParse(Environment.GetEnvironmentVariable("TESTLAB_MAX_PARALLELISM"), out var n) && n > 0 ? n : int.MaxValue;

    private static readonly JsonSerializerOptions SseJson = new(ScenarioRunner.Json) { WriteIndented = false };

    private static async Task Sse(HttpContext http, string type, object data)
    {
        await http.Response.WriteAsync($"event: {type}\ndata: {JsonSerializer.Serialize(data, SseJson)}\n\n", http.RequestAborted);
        await http.Response.Body.FlushAsync(http.RequestAborted);
    }

    private static bool CryptoEquals(string a, string b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
