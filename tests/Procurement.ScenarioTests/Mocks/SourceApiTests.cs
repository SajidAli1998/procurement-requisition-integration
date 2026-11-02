using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Procurement.ScenarioTests.Infrastructure;

namespace Procurement.ScenarioTests.Mocks;

public sealed class SourceApiTests : IDisposable
{
    private readonly SourceApiFactory _factory = new();
    private readonly HttpClient _client;

    public SourceApiTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task<(List<JsonObject> Items, string? Cursor, bool HasMore)> Page(string? cursor, int limit)
    {
        var url = $"/api/requisitions?limit={limit}" + (cursor is null ? "" : $"&cursor={cursor}");
        var page = (await _client.GetFromJsonAsync<JsonObject>(url, TestContext.Current.CancellationToken))!;
        return (page["items"]!.AsArray().Select(n => n!.AsObject()).ToList(),
            page["nextCursor"]?.GetValue<string>(), page["hasMore"]!.GetValue<bool>());
    }

    private async Task<(List<JsonObject> Items, string? Cursor)> TraverseAll(string? cursor = null, int limit = 37)
    {
        var all = new List<JsonObject>();
        while (true)
        {
            var (items, next, hasMore) = await Page(cursor, limit);
            all.AddRange(items);
            cursor = next;
            if (!hasMore) return (all, cursor);
        }
    }

    private Task Edit(string id, object patch) =>
        _client.PostAsJsonAsync($"/_admin/requisitions/{id}/edit", patch, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Full_traversal_returns_every_record_exactly_once()
    {
        var (items, _) = await TraverseAll();

        items.Count.ShouldBe(250);
        var ids = items.Select(i => i["id"]?.GetValue<string>()).Where(id => id is not null).ToList();
        ids.Count.ShouldBe(249, "one seeded record deliberately has no id");
        ids.Distinct().Count().ShouldBe(249);
    }

    [Fact]
    public async Task Final_cursor_is_a_durable_resume_point_for_the_next_run()
    {
        var (_, cursor) = await TraverseAll();
        cursor.ShouldNotBeNull();

        var (empty, _, _) = await Page(cursor, 50);
        empty.ShouldBeEmpty();

        await Edit("REQ-2026-000010", new { justification = "changed" });
        var (changed, _, hasMore) = await Page(cursor, 50);

        changed.Count.ShouldBe(1);
        changed[0]["id"]!.GetValue<string>().ShouldBe("REQ-2026-000010");
        hasMore.ShouldBeFalse();
    }

    [Fact]
    public async Task Records_edited_mid_traversal_are_never_skipped()
    {
        var (firstHalf, cursor, _) = await Page(null, 100);
        var alreadyRead = firstHalf[0]["id"]!.GetValue<string>();
        var allIds = (await TraverseAll()).Items.Select(i => i["id"]?.GetValue<string>()).ToHashSet();
        var notYetRead = allIds.First(id => id is not null && firstHalf.All(i => i["id"]?.GetValue<string>() != id))!;

        await Edit(alreadyRead, new { justification = "edited after read" });
        await Edit(notYetRead, new { justification = "edited before read" });

        var (rest, _) = await TraverseAll(cursor);
        var seen = firstHalf.Concat(rest).Select(i => i["id"]?.GetValue<string>()).ToList();

        seen.ShouldContain(notYetRead);
        seen.Count(id => id == alreadyRead).ShouldBe(2, "the newer version is delivered again; consumers dedupe on (id, version)");
        allIds.ShouldAllBe(id => seen.Contains(id));
    }

    [Fact]
    public async Task Page_overlap_fault_repeats_the_previous_pages_last_record()
    {
        await _client.PutAsJsonAsync("/_admin/faults", new { pageOverlap = true }, TestContext.Current.CancellationToken);

        var (page1, cursor, _) = await Page(null, 10);
        var (page2, _, _) = await Page(cursor, 10);

        page2.Count.ShouldBe(11);
        page2[0]["id"]!.GetValue<string>().ShouldBe(page1[^1]["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Erp_status_write_back_requires_a_current_etag()
    {
        var ct = TestContext.Current.CancellationToken;
        var body = new { erpReference = "0010000001", erpStatus = "Created", erpStatusAt = DateTimeOffset.UtcNow };
        const string url = "/api/requisitions/REQ-2026-000001/erp-status";

        (await _client.PutAsJsonAsync(url, body, ct)).StatusCode.ShouldBe(HttpStatusCode.PreconditionRequired);

        var get = await _client.GetAsync("/api/requisitions/REQ-2026-000001", ct);
        var etag = get.Headers.ETag!.Tag;

        var stale = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body) };
        stale.Headers.IfMatch.ParseAdd("\"999\"");
        var staleResponse = await _client.SendAsync(stale, ct);
        staleResponse.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        staleResponse.Headers.ETag!.Tag.ShouldBe(etag);

        var ok = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body) };
        ok.Headers.IfMatch.ParseAdd(etag);
        var okResponse = await _client.SendAsync(ok, ct);
        okResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        okResponse.Headers.ETag!.Tag.ShouldNotBe(etag);
    }

    [Fact]
    public async Task Seed_manifest_lists_every_deliberate_defect()
    {
        var manifest = (await _client.GetFromJsonAsync<JsonArray>("/_admin/seed-manifest", TestContext.Current.CancellationToken))!;
        var defects = manifest.Select(m => m!["defect"]?.GetValue<string>()).Where(d => d is not null).ToList();

        defects.Count.ShouldBe(23);
        defects.Distinct().Count().ShouldBe(11, "10 malformation types plus the SAP plant/company mismatch");
    }
}
