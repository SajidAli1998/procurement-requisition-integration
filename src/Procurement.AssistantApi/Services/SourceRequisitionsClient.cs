using System.Net;
using System.Text.Json.Nodes;

namespace Procurement.AssistantApi.Services;

public enum WithdrawOutcome { Withdrawn, NotFound, PreconditionFailed, NotAllowed }

public sealed class SourceRequisitionsClient(HttpClient http)
{
    public async Task<(JsonObject Body, string ETag)> CreateAsync(JsonObject body, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("api/requisitions", body, ct);
        response.EnsureSuccessStatusCode();
        return ((await response.Content.ReadFromJsonAsync<JsonObject>(ct))!, response.Headers.ETag?.Tag ?? "");
    }

    public async Task<(JsonObject Body, string ETag)?> GetAsync(string id, CancellationToken ct)
    {
        using var response = await http.GetAsync($"api/requisitions/{Uri.EscapeDataString(id)}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return ((await response.Content.ReadFromJsonAsync<JsonObject>(ct))!, response.Headers.ETag?.Tag ?? "");
    }

    public async Task<(List<JsonObject> Items, string? NextCursor, bool HasMore)> ListByRequesterAsync(string requester, string? cursor, int limit, CancellationToken ct)
    {
        var url = $"api/requisitions?requester={Uri.EscapeDataString(requester)}&limit={limit}" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
        var page = (await http.GetFromJsonAsync<JsonObject>(url, ct))!;
        return (page["items"]!.AsArray().Select(n => n!.AsObject()).ToList(),
            page["nextCursor"]?.GetValue<string>(), page["hasMore"]?.GetValue<bool>() == true);
    }

    public async Task<(JsonObject Body, string ETag)?> FindByRequestKeyAsync(string requester, string key, CancellationToken ct)
    {
        string? cursor = null;
        while (true)
        {
            var (items, next, hasMore) = await ListByRequesterAsync(requester, cursor, 100, ct);
            var match = items.FirstOrDefault(i => i["assistantRequestKey"]?.GetValue<string>() == key);
            if (match is not null) return await GetAsync(match["id"]!.GetValue<string>(), ct);
            if (!hasMore) return null;
            cursor = next;
        }
    }

    public async Task<(WithdrawOutcome Outcome, JsonObject? Body, string? ETag)> WithdrawAsync(string id, string etag, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/requisitions/{Uri.EscapeDataString(id)}/withdraw");
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        using var response = await http.SendAsync(request, ct);
        var newEtag = response.Headers.ETag?.Tag;
        return response.StatusCode switch
        {
            HttpStatusCode.OK => (WithdrawOutcome.Withdrawn, await response.Content.ReadFromJsonAsync<JsonObject>(ct), newEtag),
            HttpStatusCode.NotFound => (WithdrawOutcome.NotFound, null, null),
            HttpStatusCode.PreconditionFailed or HttpStatusCode.PreconditionRequired => (WithdrawOutcome.PreconditionFailed, null, newEtag),
            HttpStatusCode.Conflict => (WithdrawOutcome.NotAllowed, null, newEtag),
            _ => throw new HttpRequestException($"Withdraw failed with HTTP {(int)response.StatusCode}"),
        };
    }
}
