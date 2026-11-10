using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Polly.CircuitBreaker;
using Procurement.Sync.Http;

namespace Procurement.Sync.Clients;

public enum SapCallStatus
{
    Ok,
    Created,
    /// <summary>201 replayed by the server for a repeated Idempotency-Key.</summary>
    Replayed,
    NotFound,
    Rejected,
    PreconditionFailed,
    /// <summary>409 business conflict, for example the requisition is already released.</summary>
    Locked,
    /// <summary>409 for an Idempotency-Key whose first request is still running.</summary>
    InProgress,
    Duplicate,
    /// <summary>409: SAP knows a newer source version than the one sent.</summary>
    VersionConflict,
    /// <summary>429/5xx that outlasted all transport retries.</summary>
    Transient,
    /// <summary>No response for a write, so it may or may not have been applied.</summary>
    Ambiguous,
    CircuitOpen,
    /// <summary>401/403: a configuration problem, not a problem with the requisition.</summary>
    Unauthorized,
}

public sealed record SapCall<T>(SapCallStatus Status, T? Value = default, string? ETag = null, string? Error = null, int? HttpStatus = null)
{
    public bool IsSuccess => Status is SapCallStatus.Ok or SapCallStatus.Created or SapCallStatus.Replayed;

    // HTTP status of every attempt the transport made, retries included; 0 means no response.
    public IReadOnlyList<int> Attempts { get; init; } = [];

    public string? ExistingDocument { get; init; }
    public long? CurrentVersion { get; init; }

    public string? Location { get; init; }
}

public sealed class SapClient(HttpClient http)
{
    private const string EntitySet = "A_PurchaseRequisitionHeader";

    public Task<SapCall<SapRequisitionDto>> CreateAsync(SapRequisitionInput input, string idempotencyKey, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, EntitySet) { Content = JsonContent.Create(input, options: SapJson.Options) };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return SendAsync<SapRequisitionDto>(request, isWrite: true, ct);
    }

    public async Task<SapCall<IReadOnlyList<SapRequisitionDto>>> FindByExternalReferenceAsync(string reference, CancellationToken ct)
    {
        var filter = Uri.EscapeDataString($"ExternalReference eq '{reference.Replace("'", "''")}'");
        var call = await SendAsync<ODataCollection>(new HttpRequestMessage(HttpMethod.Get, $"{EntitySet}?$filter={filter}"), isWrite: false, ct);
        return new SapCall<IReadOnlyList<SapRequisitionDto>>(call.Status, call.Value?.Value, call.ETag, call.Error, call.HttpStatus);
    }

    public Task<SapCall<SapRequisitionDto>> GetAsync(string number, CancellationToken ct) =>
        SendAsync<SapRequisitionDto>(new HttpRequestMessage(HttpMethod.Get, $"{EntitySet}('{number}')"), isWrite: false, ct);

    public Task<SapCall<object>> PatchAsync(string number, string etag, SapRequisitionInput patch, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, $"{EntitySet}('{number}')") { Content = JsonContent.Create(patch, options: SapJson.Options) };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return SendAsync<object>(request, isWrite: true, ct);
    }

    public async Task<SapCall<(IReadOnlyList<SapRequisitionDto> Items, string? NextLink)>> GetChangesAsync(DateTime? since, string? nextLink, int top, CancellationToken ct)
    {
        var url = nextLink ?? $"{EntitySet}?$top={top}" +
            (since is null ? "" : "&$filter=" + Uri.EscapeDataString($"LastChangeDateTime gt {since.Value:O}"));
        var call = await SendAsync<ODataCollection>(new HttpRequestMessage(HttpMethod.Get, url), isWrite: false, ct);
        return new SapCall<(IReadOnlyList<SapRequisitionDto>, string?)>(call.Status,
            call.Value is null ? default : (call.Value.Value, call.Value.NextLink), call.ETag, call.Error, call.HttpStatus);
    }

    private async Task<SapCall<T>> SendAsync<T>(HttpRequestMessage request, bool isWrite, CancellationToken ct)
    {
        var attempts = SapAttemptRecorder.Track(request);
        var call = await SendCoreAsync<T>(request, isWrite, ct);
        return call with { Attempts = attempts.ToArray() };
    }

    private async Task<SapCall<T>> SendCoreAsync<T>(HttpRequestMessage request, bool isWrite, CancellationToken ct)
    {
        try
        {
            using var response = await http.SendAsync(request, ct);
            var etag = response.Headers.ETag?.Tag;
            var status = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                var replayed = response.Headers.TryGetValues("Idempotent-Replayed", out var r) && r.Contains("true");
                T? value = response.StatusCode == HttpStatusCode.NoContent || typeof(T) == typeof(object)
                    ? default
                    : await response.Content.ReadFromJsonAsync<T>(SapJson.Options, ct);
                var outcome = response.StatusCode == HttpStatusCode.Created
                    ? (replayed ? SapCallStatus.Replayed : SapCallStatus.Created)
                    : SapCallStatus.Ok;
                return new SapCall<T>(outcome, value, etag, HttpStatus: status) { Location = response.Headers.Location?.ToString() };
            }

            var (code, message, inner) = await ReadErrorAsync(response, ct);
            var mapped = response.StatusCode switch
            {
                HttpStatusCode.NotFound => SapCallStatus.NotFound,
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => SapCallStatus.Unauthorized,
                HttpStatusCode.PreconditionFailed => SapCallStatus.PreconditionFailed,
                HttpStatusCode.Conflict when code == "IDEMPOTENCY/IN_PROGRESS" => SapCallStatus.InProgress,
                HttpStatusCode.Conflict when code == "DUPLICATE_REFERENCE" => SapCallStatus.Duplicate,
                HttpStatusCode.Conflict when code == "VERSION_CONFLICT" => SapCallStatus.VersionConflict,
                HttpStatusCode.Conflict => SapCallStatus.Locked,
                HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout => SapCallStatus.Transient,
                _ when status >= 500 => SapCallStatus.Transient,
                _ => SapCallStatus.Rejected,
            };
            return new SapCall<T>(mapped, default, etag, message ?? $"HTTP {status}", status)
            {
                ExistingDocument = inner is { } e && e.TryGetProperty("existingDocument", out var doc) && doc.ValueKind == JsonValueKind.String ? doc.GetString() : null,
                CurrentVersion = inner is { } c && c.TryGetProperty("currentVersion", out var v) && v.TryGetInt64(out var n) ? n : null,
            };
        }
        catch (BrokenCircuitException ex)
        {
            return new SapCall<T>(SapCallStatus.CircuitOpen, Error: ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // No response: for a read nothing changed, for a write we cannot know.
            return new SapCall<T>(isWrite ? SapCallStatus.Ambiguous : SapCallStatus.Transient, Error: $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            request.Dispose();
        }
    }

    private static async Task<(string? Code, string? Message, JsonElement? Inner)> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (body.TryGetProperty("error", out var error))
            {
                var code = error.TryGetProperty("code", out var c) ? c.GetString() : null;
                var message = error.TryGetProperty("message", out var m) && m.TryGetProperty("value", out var v) ? v.GetString() : null;
                JsonElement? inner = error.TryGetProperty("innererror", out var i) && i.ValueKind == JsonValueKind.Object ? i.Clone() : null;
                return (code, message, inner);
            }
            if (body.TryGetProperty("title", out var title)) return (null, title.GetString(), null);
        }
        catch (JsonException)
        {
        }
        return (null, null, null);
    }

    private sealed record ODataCollection
    {
        [System.Text.Json.Serialization.JsonPropertyName("value")]
        public List<SapRequisitionDto> Value { get; init; } = [];

        [System.Text.Json.Serialization.JsonPropertyName("@odata.nextLink")]
        public string? NextLink { get; init; }
    }
}
