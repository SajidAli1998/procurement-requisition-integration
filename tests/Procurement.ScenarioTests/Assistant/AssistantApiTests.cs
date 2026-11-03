using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Procurement.AssistantApi.Model;
using Procurement.ScenarioTests.Infrastructure;

namespace Procurement.ScenarioTests.Assistant;

/// <summary>The AI-facing contract: safety rules are enforced by the server, whatever the caller sends.</summary>
public sealed class AssistantApiTests
{
    // Not one of the seeded requesters, so "my requests" only contains what these tests create.
    private const string User = "assistant.user@example.test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object ValidRequest(int laptops = 2, string plant = "1100") => new
    {
        costCenter = "CC-1000-IT",
        plant,
        neededBy = DateTime.UtcNow.AddDays(30).ToString("yyyy-MM-dd"),
        justification = "Laptops for two new starters in IT",
        lines = new[] { new { materialCode = "MAT-100001", quantity = laptops }, new { materialCode = "MAT-100003", quantity = 2 } },
        assistantSessionRef = "conv-42",
    };

    /// <summary>Validates first and, like a well-behaved assistant whose user said yes, sends the confirmation token it got back.</summary>
    internal static async Task<HttpResponseMessage> CreateAsync(HttpClient client, object body, string? key = "key-1", bool confirm = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/requisition-requests") { Content = JsonContent.Create(body) };
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        if (confirm && await ConfirmationTokenAsync(client, body) is { } token) request.Headers.Add("X-User-Confirmation", token);
        return await client.SendAsync(request, Ct);
    }

    internal static async Task<string?> ConfirmationTokenAsync(HttpClient client, object body)
    {
        var validated = await client.PostAsJsonAsync("/v1/requisition-requests:validate", body, Ct);
        return validated.IsSuccessStatusCode ? (await Json(validated))["confirmationToken"]?.GetValue<string>() : null;
    }

    internal static async Task<JsonObject> Json(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;

    [Fact(DisplayName = "A01 Validate returns every issue with a field, code and suggestion")]
    public async Task Validate_reports_actionable_issues()
    {
        await using var h = new SyncHarness();
        var client = h.Assistant.ClientFor(User);

        var response = await client.PostAsJsonAsync("/v1/requisition-requests:validate", new
        {
            costCenter = "CC-1000-IT",
            plant = "2100",
            neededBy = "2020-01-01",
            justification = "short",
            lines = new[] { new { materialCode = "MAT-999", quantity = 1m }, new { materialCode = "MAT-100002", quantity = 2.5m } },
        }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = (await response.Content.ReadFromJsonAsync<ValidationResult>(Ct))!;
        result.Valid.ShouldBeFalse();
        result.Issues.Select(i => i.Code).ShouldBe(
            ["PLANT_NOT_IN_COMPANY", "NEEDED_BY_OUT_OF_RANGE", "JUSTIFICATION_LENGTH", "UNKNOWN_MATERIAL", "INVALID_QUANTITY"], ignoreOrder: true);
        result.Issues.ShouldAllBe(i => i.Suggestion != null);
        result.Issues.Single(i => i.Code == "INVALID_QUANTITY").Field.ShouldBe("lines[1].quantity");
    }

    [Fact(DisplayName = "A02 Spend above the assistant limit is refused with a way forward")]
    public async Task Spend_limit()
    {
        await using var h = new SyncHarness();
        var response = await CreateAsync(h.Assistant.ClientFor(User), ValidRequest(laptops: 30));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = await Json(response);
        problem["code"]!.GetValue<string>().ShouldBe("VALIDATION_FAILED");
        problem["retryable"]!.GetValue<bool>().ShouldBeFalse();
        problem["invalidFields"]![0]!["code"]!.GetValue<string>().ShouldBe("SPEND_LIMIT_EXCEEDED");
    }

    [Fact(DisplayName = "A03 Create needs an Idempotency-Key; retries replay, a changed body is rejected")]
    public async Task Idempotent_create()
    {
        await using var h = new SyncHarness();
        var client = h.Assistant.ClientFor(User);

        (await Json(await CreateAsync(client, ValidRequest(), key: null)))["code"]!.GetValue<string>().ShouldBe("IDEMPOTENCY_KEY_REQUIRED");

        var first = await CreateAsync(client, ValidRequest());
        var retry = await CreateAsync(client, ValidRequest());
        var changed = await CreateAsync(client, ValidRequest(laptops: 3));

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = await Json(first);
        created["status"]!.GetValue<string>().ShouldBe("PendingApproval");
        created["totalAmount"]!.GetValue<decimal>().ShouldBe(2 * 1150m + 2 * 170m, "prices come from the catalogue");
        retry.StatusCode.ShouldBe(HttpStatusCode.Created);
        retry.Headers.GetValues("Idempotent-Replayed").ShouldContain("true");
        (await Json(retry))["id"]!.GetValue<string>().ShouldBe(created["id"]!.GetValue<string>());
        changed.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await Json(changed))["code"]!.GetValue<string>().ShouldBe("IDEMPOTENCY_KEY_REUSED");

        var mine = await client.GetFromJsonAsync<JsonObject>("/v1/requisition-requests?limit=50", Ct);
        mine!["items"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact(DisplayName = "A04 Unknown fields such as a made-up price are rejected")]
    public async Task Strict_schema()
    {
        await using var h = new SyncHarness();
        var response = await CreateAsync(h.Assistant.ClientFor(User), new
        {
            costCenter = "CC-1000-IT",
            plant = "1100",
            neededBy = "2026-12-01",
            justification = "Laptops for starters",
            lines = new[] { new { materialCode = "MAT-100001", quantity = 1, unitPrice = 1 } },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await Json(response);
        problem["code"]!.GetValue<string>().ShouldBe("INVALID_REQUEST_BODY");
        problem["detail"]!.GetValue<string>().ShouldContain("unitPrice");

        var quotedNumber = await CreateAsync(h.Assistant.ClientFor(User), new
        {
            costCenter = "CC-1000-IT",
            plant = "1100",
            neededBy = "2026-12-01",
            justification = "Laptops for starters",
            lines = new[] { new { materialCode = "MAT-100001", quantity = "2" } },
        }, "key-2");
        quotedNumber.StatusCode.ShouldBe(HttpStatusCode.BadRequest, "numbers must be JSON numbers, not strings");
    }

    [Fact(DisplayName = "A05 Scopes and identity are enforced by the server")]
    public async Task Authorization()
    {
        await using var h = new SyncHarness();
        var owner = h.Assistant.ClientFor(User);
        var id = (await Json(await CreateAsync(owner, ValidRequest())))["id"]!.GetValue<string>();

        var anonymous = h.Assistant.CreateClient();
        (await anonymous.GetAsync($"/v1/requisition-requests/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var readOnly = h.Assistant.ClientFor(User, "requisitions:read");
        var forbidden = await CreateAsync(readOnly, ValidRequest(), "key-2");
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Json(forbidden))["code"]!.GetValue<string>().ShouldBe("FORBIDDEN");

        var someoneElse = h.Assistant.ClientFor("user99@example.test");
        var notYours = await someoneElse.GetAsync($"/v1/requisition-requests/{id}", Ct);
        notYours.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Json(notYours))["code"]!.GetValue<string>().ShouldBe("NOT_YOUR_REQUISITION");

        var delegated = h.Assistant.ClientFor("buyer@example.test", "requisitions:read", "requisitions:read:all");
        (await delegated.GetAsync($"/v1/requisition-requests/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await someoneElse.GetAsync("/v1/requisition-requests/REQ-0000-404", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact(DisplayName = "A10 Create needs the user's confirmation of exactly this request")]
    public async Task Confirmation_is_bound_to_the_request()
    {
        await using var h = new SyncHarness();
        var client = h.Assistant.ClientFor(User);

        var unconfirmed = await CreateAsync(client, ValidRequest(), confirm: false);
        unconfirmed.StatusCode.ShouldBe(HttpStatusCode.PreconditionRequired);
        (await Json(unconfirmed))["code"]!.GetValue<string>().ShouldBe("CONFIRMATION_REQUIRED");

        var validated = await Json(await client.PostAsJsonAsync("/v1/requisition-requests:validate", ValidRequest(), Ct));
        validated["confirmationSummary"]!.GetValue<string>().ShouldContain("2 EA Laptop 14-inch");
        var token = validated["confirmationToken"]!.GetValue<string>();

        // The assistant quietly changes the quantity after the user said yes.
        var changed = new HttpRequestMessage(HttpMethod.Post, "/v1/requisition-requests") { Content = JsonContent.Create(ValidRequest(laptops: 3)) };
        changed.Headers.Add("Idempotency-Key", "key-2");
        changed.Headers.Add("X-User-Confirmation", token);
        var mismatch = await client.SendAsync(changed, Ct);
        mismatch.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await Json(mismatch))["code"]!.GetValue<string>().ShouldBe("CONFIRMATION_MISMATCH");

        // Another user's token does not fit either.
        var other = new HttpRequestMessage(HttpMethod.Post, "/v1/requisition-requests") { Content = JsonContent.Create(ValidRequest()) };
        other.Headers.Add("Idempotency-Key", "key-3");
        other.Headers.Add("X-User-Confirmation", token);
        (await h.Assistant.ClientFor("other@example.test").SendAsync(other, Ct)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var mine = await client.GetFromJsonAsync<JsonObject>("/v1/requisition-requests?limit=50", Ct);
        mine!["items"]!.AsArray().Count.ShouldBe(0, "nothing was created");
    }

    [Fact(DisplayName = "A06 Withdraw needs the current ETag and only works before approval")]
    public async Task Withdraw()
    {
        await using var h = new SyncHarness();
        var client = h.Assistant.ClientFor(User);
        var id = (await Json(await CreateAsync(client, ValidRequest())))["id"]!.GetValue<string>();

        async Task<HttpResponseMessage> WithdrawAsync(string requisition, string? etag)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/requisition-requests/{requisition}:withdraw");
            if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
            return await client.SendAsync(request, Ct);
        }

        (await WithdrawAsync(id, null)).StatusCode.ShouldBe(HttpStatusCode.PreconditionRequired);
        (await WithdrawAsync(id, "\"99\"")).StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);

        var current = await client.GetAsync($"/v1/requisition-requests/{id}", Ct);
        var withdrawn = await WithdrawAsync(id, current.Headers.ETag!.Tag);
        withdrawn.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Json(withdrawn))["status"]!.GetValue<string>().ShouldBe("Withdrawn");

        var second = (await Json(await CreateAsync(client, ValidRequest(laptops: 1), "key-2")))["id"]!.GetValue<string>();
        await h.EditSourceAsync(second, new { status = "Approved" });
        var approved = await client.GetAsync($"/v1/requisition-requests/{second}", Ct);
        var refused = await WithdrawAsync(second, approved.Headers.ETag!.Tag);
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Json(refused))["code"]!.GetValue<string>().ShouldBe("NOT_WITHDRAWABLE");
    }

    [Fact(DisplayName = "A07 Journey: raised by the assistant, approved, transferred, ordered")]
    public async Task End_to_end_journey()
    {
        await using var h = new SyncHarness();
        var client = h.Assistant.ClientFor(User);
        var id = (await Json(await CreateAsync(client, ValidRequest())))["id"]!.GetValue<string>();

        await h.RunAsync();
        (await client.GetFromJsonAsync<JsonObject>($"/v1/requisition-requests/{id}", Ct))!["status"]!.GetValue<string>()
            .ShouldBe("PendingApproval", "nothing is transferred before a person approves it");

        await h.EditSourceAsync(id, new { status = "Approved", approvedAt = DateTime.UtcNow.ToString("O") });
        await h.RunAsync();
        var transferred = (await client.GetFromJsonAsync<JsonObject>($"/v1/requisition-requests/{id}", Ct))!;
        transferred["status"]!.GetValue<string>().ShouldBe("TransferredToErp");
        transferred["erpReference"]!.GetValue<string>().ShouldStartWith("00");

        for (var i = 0; i < 3; i++) await h.SapAdmin.PostAsync("/_admin/advance?rejectEvery=0", null, Ct);
        await h.RunAsync();
        var ordered = (await client.GetFromJsonAsync<JsonObject>($"/v1/requisition-requests/{id}", Ct))!;
        ordered["status"]!.GetValue<string>().ShouldBe("PurchaseOrderCreated");
        ordered["purchaseOrder"]!.GetValue<string>().ShouldStartWith("45");
        ordered["statusReason"]!.GetValue<string>().ShouldContain(ordered["purchaseOrder"]!.GetValue<string>());
        ordered["timeline"]!.AsArray().Count.ShouldBeGreaterThanOrEqualTo(4);
    }

    [Fact(DisplayName = "A08 A lost response from the source does not create a second draft")]
    public async Task Lost_source_response_is_not_duplicated()
    {
        await using var h = new SyncHarness();
        var client = h.Assistant.ClientFor(User);
        await h.ScriptSourceAsync(new { kind = "LostResponse", method = "POST", pathContains = "/api/requisitions", count = 1 });

        var first = await CreateAsync(client, ValidRequest());
        first.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await Json(first))["retryable"]!.GetValue<bool>().ShouldBeTrue();

        var retry = await CreateAsync(client, ValidRequest());
        retry.StatusCode.ShouldBe(HttpStatusCode.Created);
        var mine = await client.GetFromJsonAsync<JsonObject>("/v1/requisition-requests?limit=50", Ct);
        mine!["items"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact(DisplayName = "A09 The OpenAPI contract tells agents how each operation may be used")]
    public async Task OpenApi_carries_agent_hints()
    {
        await using var h = new SyncHarness();
        var document = (await h.Assistant.CreateClient().GetFromJsonAsync<JsonObject>("/openapi/v1.json", Ct))!;

        document["info"]!["description"]!.GetValue<string>().ShouldContain("Idempotency-Key");
        var operations = document["paths"]!.AsObject().SelectMany(p => p.Value!.AsObject().Select(o => o.Value!.AsObject())).ToList();
        operations.Count.ShouldBe(8);
        operations.ShouldAllBe(o => o["operationId"] != null && o["x-mcp"] != null && o["security"] != null);
        var destructive = operations.Where(o => o["x-mcp"]!["destructiveHint"]!.GetValue<bool>()).Select(o => o["operationId"]!.GetValue<string>());
        destructive.ShouldBe(["withdrawRequisitionRequest"]);
        operations.Count(o => o["x-mcp"]!["readOnlyHint"]!.GetValue<bool>()).ShouldBe(6);

        var schemas = document["components"]!["schemas"]!;
        schemas["CreateRequisitionRequest"]!["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
        schemas["RequestLine"]!["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
        schemas["CreateRequisitionRequest"]!["properties"]!["lines"]!["maxItems"]!.GetValue<int>().ShouldBe(20);
    }
}
