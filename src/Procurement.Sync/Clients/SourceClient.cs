using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Procurement.Sync.Clients;

public sealed record SourcePage(IReadOnlyList<JsonElement> Items, string? NextCursor, bool HasMore);

public sealed record ErpStatusWriteback(string ErpReference, string ErpStatus, DateTimeOffset ErpStatusAt, string? Message);

public enum WritebackStatus { Ok, PreconditionFailed, NotFound, Failed }

public sealed record WritebackResult(WritebackStatus Status, string? ETag = null, string? Error = null);

// Items stay raw JSON so each one can be validated on its own.
public sealed class SourceClient(HttpClient http)
{
    public async Task<SourcePage> GetChangesAsync(string? cursor, int limit, CancellationToken ct)
    {
        var url = $"api/requisitions?limit={limit}" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = document.RootElement;
            var items = root.GetProperty("items").EnumerateArray().Select(e => e.Clone()).ToList();
            var next = root.TryGetProperty("nextCursor", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            var hasMore = root.TryGetProperty("hasMore", out var h) && h.ValueKind == JsonValueKind.True;
            if (hasMore && next is null) throw new InvalidOperationException("hasMore is true but no nextCursor was given");
            return new SourcePage(items, next, hasMore);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // Treated like an outage: the page is not processed and the checkpoint stays where it was.
            throw new HttpRequestException($"The source returned an unreadable page: {ex.Message}", ex);
        }
    }

    public async Task<(JsonElement Body, string ETag)?> GetAsync(string id, CancellationToken ct)
    {
        using var response = await http.GetAsync($"api/requisitions/{Uri.EscapeDataString(id)}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return (body, response.Headers.ETag?.Tag ?? "");
    }

    public async Task<WritebackResult> PutErpStatusAsync(string id, string etag, ErpStatusWriteback update, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"api/requisitions/{Uri.EscapeDataString(id)}/erp-status")
        {
            Content = JsonContent.Create(update),
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        try
        {
            using var response = await http.SendAsync(request, ct);
            return response.StatusCode switch
            {
                HttpStatusCode.OK => new WritebackResult(WritebackStatus.Ok, response.Headers.ETag?.Tag),
                HttpStatusCode.PreconditionFailed => new WritebackResult(WritebackStatus.PreconditionFailed, response.Headers.ETag?.Tag),
                HttpStatusCode.NotFound => new WritebackResult(WritebackStatus.NotFound),
                _ => new WritebackResult(WritebackStatus.Failed, Error: $"HTTP {(int)response.StatusCode}"),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new WritebackResult(WritebackStatus.Failed, Error: ex.Message);
        }
    }
}
