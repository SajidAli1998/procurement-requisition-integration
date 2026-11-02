using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Procurement.ScenarioTests.Infrastructure;

namespace Procurement.ScenarioTests.Mocks;

public sealed class SapApiTests : IDisposable
{
    private const string EntitySet = "/sap/opu/odata/sap/API_PURCHASEREQ_PROCESS_SRV/A_PurchaseRequisitionHeader";
    private readonly SapApiFactory _factory = new();
    private readonly HttpClient _client;

    public SapApiTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Body(string reference, string plant = "1100", string description = "Laptops") => new
    {
        PurReqnDescription = description,
        ExternalReference = reference,
        ExternalVersion = 1,
        to_PurchaseReqnItem = new[]
        {
            new
            {
                Material = "MAT-100001", RequestedQuantity = 2, BaseUnit = "EA", PurchaseRequisitionPrice = 1000,
                PurReqnItemCurrency = "USD", Plant = plant, CompanyCode = "1000", DeliveryDate = "2026-12-01",
            },
        },
    };

    private Task<HttpResponseMessage> Create(object body, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, EntitySet) { Content = JsonContent.Create(body) };
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return _client.SendAsync(request, Ct);
    }

    private async Task<int> CountFor(string reference)
    {
        var result = await _client.GetFromJsonAsync<JsonObject>($"{EntitySet}?$filter=ExternalReference eq '{reference}'", Ct);
        return result!["value"]!.AsArray().Count;
    }

    private Task Script(object fault) => _client.PostAsJsonAsync("/_admin/faults/script", new[] { fault }, Ct);

    [Fact]
    public async Task Same_idempotency_key_replays_and_a_different_body_is_rejected()
    {
        var first = await Create(Body("REQ-A"), "key-1");
        var replay = await Create(Body("REQ-A"), "key-1");
        var mismatch = await Create(Body("REQ-A", description: "Different"), "key-1");

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        replay.StatusCode.ShouldBe(HttpStatusCode.Created);
        replay.Headers.GetValues("Idempotent-Replayed").ShouldContain("true");
        (await replay.Content.ReadAsStringAsync(Ct)).ShouldBe(await first.Content.ReadAsStringAsync(Ct));
        mismatch.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CountFor("REQ-A")).ShouldBe(1);
    }

    [Fact]
    public async Task Lost_response_commits_in_sap_and_a_keyed_retry_does_not_duplicate()
    {
        await Script(new { kind = "LostResponse", method = "POST" });

        await Should.ThrowAsync<Exception>(() => Create(Body("REQ-B"), "key-2"));
        (await CountFor("REQ-B")).ShouldBe(1, "SAP committed even though the client saw a failure");

        var retry = await Create(Body("REQ-B"), "key-2");
        retry.Headers.GetValues("Idempotent-Replayed").ShouldContain("true");
        (await CountFor("REQ-B")).ShouldBe(1);
    }

    [Fact]
    public async Task Without_idempotency_support_a_blind_retry_creates_a_duplicate()
    {
        // This is why the integration looks SAP up by ExternalReference before re-sending.
        await _client.PutAsJsonAsync("/_admin/faults", new { HonorIdempotencyKey = false }, Ct);
        await Script(new { kind = "LostResponse", method = "POST" });

        await Should.ThrowAsync<Exception>(() => Create(Body("REQ-C"), "key-3"));
        (await Create(Body("REQ-C"), "key-3")).StatusCode.ShouldBe(HttpStatusCode.Created);

        (await CountFor("REQ-C")).ShouldBe(2);
        var stats = await _client.GetFromJsonAsync<JsonObject>("/_admin/stats", Ct);
        stats!["duplicateExternalReferences"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public async Task Sap_rejects_a_plant_outside_the_company_code_with_an_odata_error()
    {
        var response = await Create(Body("REQ-D", plant: "2100"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<JsonObject>(Ct);
        error!["error"]!["code"]!.GetValue<string>().ShouldBe("06/402");
        error["error"]!["message"]!["value"]!.GetValue<string>().ShouldContain("not assigned to company code 1000");
    }

    [Fact]
    public async Task Patch_needs_the_current_etag_and_is_refused_once_released()
    {
        var created = await Create(Body("REQ-E"));
        var location = created.Headers.Location!.ToString();
        var number = (await created.Content.ReadFromJsonAsync<JsonObject>(Ct))!["PurchaseRequisition"]!.GetValue<string>();

        async Task<HttpResponseMessage> Patch(string? etag)
        {
            var request = new HttpRequestMessage(HttpMethod.Patch, location) { Content = JsonContent.Create(new { PurReqnDescription = "Updated" }) };
            if (etag is not null) request.Headers.IfMatch.ParseAdd(etag);
            return await _client.SendAsync(request, Ct);
        }

        (await Patch(null)).StatusCode.ShouldBe(HttpStatusCode.PreconditionRequired);
        (await Patch("\"42\"")).StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        var ok = await Patch(created.Headers.ETag!.Tag);
        ok.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await _client.PostAsJsonAsync($"/_admin/requisitions/{number}/lifecycle", new { Lifecycle = "Released" }, Ct);
        var current = await _client.GetAsync(location, Ct);
        (await Patch(current.Headers.ETag!.Tag)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Change_feed_pages_through_every_requisition_via_next_link()
    {
        for (var i = 0; i < 7; i++) await Create(Body($"REQ-F{i}"));

        var seen = new List<string>();
        string? url = $"{EntitySet}?$filter=LastChangeDateTime gt 2000-01-01T00:00:00Z&$top=3";
        while (url is not null)
        {
            var page = (await _client.GetFromJsonAsync<JsonObject>(url, Ct))!;
            seen.AddRange(page["value"]!.AsArray().Select(v => v!["ExternalReference"]!.GetValue<string>()));
            url = page["@odata.nextLink"]?.GetValue<string>();
        }

        seen.Count.ShouldBe(7);
        seen.Distinct().Count().ShouldBe(7);
    }

    [Fact]
    public async Task Advancing_moves_through_the_lifecycle_and_assigns_a_purchase_order()
    {
        var created = await Create(Body("REQ-G"));
        var location = created.Headers.Location!.ToString();

        for (var i = 0; i < 3; i++) await _client.PostAsync("/_admin/advance?rejectEvery=0", null, Ct);
        var pr = (await _client.GetFromJsonAsync<JsonObject>(location, Ct))!;

        pr["ProcessingStatus"]!.GetValue<string>().ShouldBe("B");
        pr["ReleaseStatus"]!.GetValue<string>().ShouldBe("02");
        pr["to_PurchaseReqnItem"]![0]!["PurchaseOrder"]!.GetValue<string>().ShouldStartWith("45");
    }
}
