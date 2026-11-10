using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Procurement.Scenarios.Lab;

public sealed record HttpExchange(
    long Seq,
    DateTimeOffset At,
    string Channel,
    string Host,
    string Method,
    string Url,
    int? Status,
    string? Outcome,
    double DurationMs,
    string? CorrelationId,
    string? IdempotencyKey,
    string? RequestBody,
    string? ResponseBody);

/// <summary>Bodies are truncated to keep the evidence small.</summary>
public sealed class HttpCapture
{
    private const int MaxBody = 2048;
    private readonly ConcurrentQueue<HttpExchange> _exchanges = new();
    private long _seq;

    public bool Enabled { get; set; } = true;

    public IReadOnlyList<HttpExchange> Exchanges => _exchanges.ToArray();

    public event Action<HttpExchange>? Recorded;

    internal void Add(HttpExchange exchange)
    {
        if (!Enabled) return;
        _exchanges.Enqueue(exchange);
        Recorded?.Invoke(exchange);
    }

    internal long Next() => Interlocked.Increment(ref _seq);

    public void Clear()
    {
        while (_exchanges.TryDequeue(out _)) { }
    }

    internal static string? Truncate(string? s) =>
        s is null ? null : s.Length <= MaxBody ? s : s[..MaxBody] + $"… ({s.Length - MaxBody} more characters)";
}

// Sits closest to the wire so it sees every attempt, retries included.
public sealed class CaptureHandler(HttpCapture capture, string channel) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.Headers.Contains("X-Correlation-Id")) request.Headers.TryAddWithoutValidation("X-Correlation-Id", Guid.NewGuid().ToString("N")[..12]);
        var correlation = request.Headers.GetValues("X-Correlation-Id").FirstOrDefault();
        var key = request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.FirstOrDefault() : null;
        string? requestBody = null;
        if (request.Content is not null)
        {
            await request.Content.LoadIntoBufferAsync(cancellationToken);
            requestBody = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        var seq = capture.Next();
        var at = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            string? body = null;
            if (response.Content is not null)
            {
                await response.Content.LoadIntoBufferAsync(cancellationToken);
                body = Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync(cancellationToken));
            }
            capture.Add(new HttpExchange(seq, at, channel, request.RequestUri?.Host ?? "", request.Method.Method, request.RequestUri?.PathAndQuery ?? "", (int)response.StatusCode,
                null, clock.Elapsed.TotalMilliseconds, correlation, key, HttpCapture.Truncate(requestBody), HttpCapture.Truncate(body)));
            return response;
        }
        catch (Exception ex)
        {
            capture.Add(new HttpExchange(seq, at, channel, request.RequestUri?.Host ?? "", request.Method.Method, request.RequestUri?.PathAndQuery ?? "", null,
                $"no response: {ex.GetType().Name}", clock.Elapsed.TotalMilliseconds, correlation, key, HttpCapture.Truncate(requestBody), null));
            throw;
        }
    }
}
