using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Procurement.Scenarios.Fixtures;

/// <summary>Calls the assistant API the way an AI assistant would: validate, show the user, then create with their confirmation.</summary>
public static class Assist
{
    public const string ConfirmationHeader = "X-User-Confirmation";

    /// <summary>Read from the API itself so fixtures never invent codes.</summary>
    public sealed record Reference(string Material, decimal Price, string CostCenter, string Plant, List<(string Code, decimal Price)> Materials);

    public static async Task<Reference> ReferenceAsync(HttpClient client)
    {
        var materials = (await client.GetFromJsonAsync<JsonArray>("/v1/reference/materials?limit=50"))!
            .Select(m => (m!["code"]!.GetValue<string>(), m["unitPrice"]!.GetValue<decimal>())).ToList();
        var center = (await client.GetFromJsonAsync<JsonArray>("/v1/reference/cost-centers"))![0]!;
        var company = center["companyCode"]!.GetValue<string>();
        var plant = (await client.GetFromJsonAsync<JsonArray>("/v1/reference/plants"))!.First(p => p!["companyCode"]!.GetValue<string>() == company)!;
        var cheapest = materials.OrderBy(i => i.Item2).First();
        return new Reference(cheapest.Item1, cheapest.Item2, center["code"]!.GetValue<string>(), plant["code"]!.GetValue<string>(), materials);
    }

    public static JsonObject Draft(Reference r, int quantity = 1, string? material = null, string? justification = null, int lines = 1)
    {
        var array = new JsonArray();
        for (var i = 0; i < lines; i++)
            array.Add(new JsonObject { ["materialCode"] = i == 0 ? material ?? r.Material : r.Materials[i % r.Materials.Count].Code, ["quantity"] = quantity });
        return new JsonObject
        {
            ["costCenter"] = r.CostCenter,
            ["plant"] = r.Plant,
            ["neededBy"] = Req.Today(30),
            ["justification"] = justification ?? "Replacement equipment requested through the assistant",
            ["lines"] = array,
            ["assistantSessionRef"] = "lab-" + Guid.NewGuid().ToString("N")[..8],
        };
    }

    public static async Task<(HttpResponseMessage Response, JsonObject? Body)> ValidateAsync(HttpClient client, JsonObject draft)
    {
        var response = await client.PostAsJsonAsync("/v1/requisition-requests:validate", draft);
        return (response, await ReadAsync(response));
    }

    public static async Task<string?> TokenAsync(HttpClient client, JsonObject draft) =>
        (await ValidateAsync(client, draft)).Body?["confirmationToken"]?.GetValue<string>();

    public static async Task<(HttpResponseMessage Response, JsonObject? Body)> CreateAsync(HttpClient client, JsonObject draft, string? key, string? confirmation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/requisition-requests") { Content = JsonContent.Create(draft) };
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        if (confirmation is not null) request.Headers.Add(ConfirmationHeader, confirmation);
        var response = await client.SendAsync(request);
        return (response, await ReadAsync(response));
    }

    /// <summary>Validate, then create with the token the user's "yes" stands for.</summary>
    public static async Task<(HttpResponseMessage Response, JsonObject? Body)> ConfirmedCreateAsync(HttpClient client, JsonObject draft, string? key = null)
    {
        var token = await TokenAsync(client, draft);
        return await CreateAsync(client, draft, key ?? Guid.NewGuid().ToString(), token);
    }

    public static async Task<JsonObject?> ReadAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public static string? Code(JsonObject? problem) => problem?["code"]?.GetValue<string>() ?? problem?["errorCode"]?.GetValue<string>();
}
