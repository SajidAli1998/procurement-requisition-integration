using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Procurement.TestLab.Tests;

public sealed class TestLabFactory : WebApplicationFactory<TestLabMarker>
{
    public const string Key = "test-lab-key-0123456789abcdef";

    static TestLabFactory()
    {
        Environment.SetEnvironmentVariable("TESTLAB_KEY", Key);
        Environment.SetEnvironmentVariable("TESTLAB_RESULTS_DIR", Path.Combine(Path.GetTempPath(), "testlab-tests", Guid.NewGuid().ToString("N")));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseContentRoot(AppContext.BaseDirectory);

    public HttpClient Keyed()
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Add(Api.KeyHeader, Key);
        return c;
    }
}

public sealed class TestLabApiTests(TestLabFactory factory) : IClassFixture<TestLabFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly HashSet<string> ReadOnlyPosts = ["/api/workspace/validate", "/api/generator/preview"];

    [Fact(DisplayName = "T01 Every lab-changing endpoint refuses a request without the lab key (SEC-026)")]
    public async Task Mutations_need_the_key()
    {
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? []).Select(m => (Method: m, Route: "/" + e.RoutePattern.RawText!.TrimStart('/'))))
            .Where(x => x.Route.StartsWith("/api/") && x.Method is "POST" or "PUT" or "DELETE" && !ReadOnlyPosts.Contains(x.Route))
            .ToList();
        endpoints.Count.ShouldBeGreaterThan(20);
        var client = factory.CreateClient();
        var open = new List<string>();
        foreach (var (method, route) in endpoints)
        {
            var path = route.Replace("{id}", "X").Replace("{target}", "sap").Replace("{number}", "1").Replace("{lifecycle}", "Released").Replace("{format}", "json").Replace("{scenario}", "X");
            using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { }) };
            var response = await client.SendAsync(request, Ct);
            if (response.StatusCode != HttpStatusCode.Unauthorized) open.Add($"{method} {route} returned {(int)response.StatusCode}");
        }
        open.ShouldBeEmpty();
    }

    [Fact(DisplayName = "T02 Reads work without the key; the catalogue lists every scenario")]
    public async Task Reads_and_catalogue()
    {
        var client = factory.CreateClient();
        (await client.GetAsync("/api/health", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var catalogue = (await client.GetFromJsonAsync<JsonObject>("/api/catalogue", Ct))!;
        catalogue["scenarios"]!.AsArray().Count.ShouldBeGreaterThan(350);
        catalogue["invariants"]!.AsArray().Count.ShouldBe(12);
    }

    [Fact(DisplayName = "T03 A batch started through the API runs the real scenario and stores its result")]
    public async Task Batch_runs_end_to_end()
    {
        var client = factory.Keyed();
        var accepted = await client.PostAsJsonAsync("/api/batches", new { ids = new[] { "VAL-001", "APR-009" }, name = "api test" }, Ct);
        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        JsonObject? latest = null;
        for (var i = 0; i < 120 && latest is null; i++)
        {
            await Task.Delay(500, Ct);
            var history = (await client.GetFromJsonAsync<JsonObject>("/api/batches", Ct))!["history"]!.AsArray();
            if (history.Any(h => h!["name"]!.GetValue<string>() == "api test")) latest = (await client.GetFromJsonAsync<JsonObject>("/api/batches/latest", Ct))!;
        }
        latest.ShouldNotBeNull();
        var results = latest["results"]!.AsArray();
        results.Single(r => r!["id"]!.GetValue<string>() == "VAL-001")!["status"]!.GetValue<string>().ShouldBe("Pass");
        results.Single(r => r!["id"]!.GetValue<string>() == "APR-009")!["status"]!.GetValue<string>().ShouldBe("Blocked");
    }

    [Fact(DisplayName = "T04 The workspace syncs the client's workbook to the baseline numbers")]
    public async Task Workspace_baseline()
    {
        var client = factory.Keyed();
        (await client.PostAsJsonAsync("/api/workspace/reset", new { dataset = "Workbook" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.PostAsync("/api/workspace/sync", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        JsonObject? totals = null;
        for (var i = 0; i < 120; i++)
        {
            await Task.Delay(500, Ct);
            var ws = (await client.GetFromJsonAsync<JsonObject>("/api/workspace", Ct))!;
            if (ws["active"] is null)
            {
                totals = (await client.GetFromJsonAsync<JsonObject>("/api/workspace/dashboard", Ct))!["totals"]!.AsObject();
                if (totals["created"]!.GetValue<int>() > 0) break;
            }
        }
        totals.ShouldNotBeNull();
        totals["created"]!.GetValue<int>().ShouldBe(58);
        totals["linked"]!.GetValue<int>().ShouldBe(7);
        totals["quarantined"]!.GetValue<int>().ShouldBe(50);
        totals["skipped"]!.GetValue<int>().ShouldBe(5);
        totals["duplicateReferencesInSap"]!.GetValue<int>().ShouldBe(0);
        var recon = (await client.GetFromJsonAsync<JsonObject>("/api/workspace/reconciliation", Ct))!;
        recon["comparison"]!["matched"]!.GetValue<int>().ShouldBe(120);
    }

    [Fact(DisplayName = "T05 Import and generator endpoints report precisely")]
    public async Task Import_and_generator()
    {
        var client = factory.Keyed();
        var preview = (await (await client.PostAsJsonAsync("/api/generator/preview", new { count = 50, seed = 3 }, Ct)).Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        preview["records"]!.AsArray().Count.ShouldBe(50);
        using var form = new MultipartFormDataContent { { new ByteArrayContent("requisition_id,status\n=1+1,Approved\n"u8.ToArray()), "file", "../../evil.csv" } };
        var imported = (await (await client.PostAsync("/api/workspace/import", form, Ct)).Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        imported["fileName"]!.GetValue<string>().ShouldBe("evil.csv");
        imported["ok"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact(DisplayName = "T06 What if: the network drops mid-sync, then comes back; nothing lost or duplicated")]
    public async Task What_if_network_down_and_back()
    {
        var client = factory.Keyed();
        (await client.GetFromJsonAsync<JsonArray>("/api/whatif", Ct))!.Count.ShouldBe(7);
        var down = (await (await client.PostAsync("/api/whatif/network-down", null, Ct)).Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        down["checks"]!.AsArray().ShouldAllBe(c => c!["ok"]!.GetValue<bool>());
        Number(down, "Waiting to be sent").ShouldBeGreaterThan(0);
        down["next"]!["id"]!.GetValue<string>().ShouldBe("recover");
        var back = (await (await client.PostAsync("/api/whatif/recover", null, Ct)).Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        back["checks"]!.AsArray().ShouldAllBe(c => c!["ok"]!.GetValue<bool>());
        Number(back, "Created in SAP").ShouldBe(58);
        Number(back, "Linked to existing SAP documents").ShouldBe(7);
        Number(back, "Waiting to be sent").ShouldBe(0);
        Number(back, "Duplicates in SAP").ShouldBe(0);
    }

    private static int Number(JsonObject story, string label) =>
        story["numbers"]!.AsArray().Single(n => n!["label"]!.GetValue<string>() == label)!["after"]!.GetValue<int>();

    [Fact(DisplayName = "T07 Hands-on: pause holds the sync, SAP down leaves work waiting, reconnecting finishes it")]
    public async Task Hands_on_pause_network_and_checks()
    {
        var client = factory.Keyed();
        try
        {
            await HandsOnFlow(client);
        }
        finally
        {
            // Never leave the shared workspace paused or disconnected for the other tests.
            await client.PostAsync("/api/workspace/resume", null, CancellationToken.None);
            await client.PostAsJsonAsync("/api/workspace/network", new { sap = "up", source = "up", slowMotion = false }, CancellationToken.None);
        }
    }

    private static async Task HandsOnFlow(HttpClient client)
    {
        await client.PostAsJsonAsync("/api/workspace/reset", new { dataset = "Workbook" }, Ct);
        var fresh = await Checks(client);
        fresh["verdict"]!["tone"]!.GetValue<string>().ShouldBe("neutral", "nothing has been read yet, so nothing can be lost");
        await client.PostAsJsonAsync("/api/workspace/network", new { sap = "up", source = "up", slowMotion = true }, Ct);
        (await client.PostAsync("/api/workspace/sync", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await Task.Delay(3000, Ct);
        await client.PostAsync("/api/workspace/pause", null, Ct);
        // Requisitions already on their way finish (up to one lookup and one create each, 0.7 s per call in slow motion).
        await Task.Delay(4000, Ct);
        var paused = await Checks(client);
        paused["paused"]!.GetValue<bool>().ShouldBeTrue();
        var held = paused["counts"]!["inSap"]!.GetValue<int>();
        await Task.Delay(3000, Ct);
        (await Checks(client))["counts"]!["inSap"]!.GetValue<int>().ShouldBe(held, "nothing new may reach SAP while paused");

        await client.PostAsJsonAsync("/api/workspace/network", new { sap = "down", source = "up", slowMotion = true }, Ct);
        await client.PostAsync("/api/workspace/resume", null, Ct);
        await WaitIdle(client);
        var down = await Checks(client);
        down["verdict"]!["tone"]!.GetValue<string>().ShouldBe("warning");
        down["counts"]!["duplicates"]!.GetValue<int>().ShouldBe(0);

        await client.PostAsJsonAsync("/api/workspace/network", new { sap = "up", source = "up", slowMotion = false }, Ct);
        for (var i = 0; i < 4 && (await Checks(client))["verdict"]!["tone"]!.GetValue<string>() != "success"; i++)
        {
            await client.PostAsync("/api/workspace/sync", null, Ct);
            await WaitIdle(client);
            await Task.Delay(2500, Ct);
        }
        var done = await Checks(client);
        done["verdict"]!["tone"]!.GetValue<string>().ShouldBe("success", done["verdict"]!["text"]!.GetValue<string>());
        done["counts"]!["inSap"]!.GetValue<int>().ShouldBe(65);
        done["checks"]!.AsArray().ShouldAllBe(c => c!["ok"]!.GetValue<bool>());
    }

    private static async Task<JsonObject> Checks(HttpClient client) => (await client.GetFromJsonAsync<JsonObject>("/api/workspace/checks", Ct))!;

    private static async Task WaitIdle(HttpClient client)
    {
        for (var i = 0; i < 120; i++)
        {
            if ((await client.GetFromJsonAsync<JsonObject>("/api/workspace", Ct))!["active"] is null) return;
            await Task.Delay(500, Ct);
        }
    }
}
