namespace Mocks.SapApi;

/// <summary>SAP Gateway style error body: { "error": { "code", "message": { "lang", "value" } } }.</summary>
public static class ODataError
{
    /// <param name="facts">Machine-readable extras added to innererror, e.g. the existing document id on a duplicate.</param>
    public static IResult Create(int status, string code, string message, IEnumerable<string>? details = null,
        IReadOnlyDictionary<string, object>? facts = null)
    {
        var inner = new Dictionary<string, object>
        {
            ["errordetails"] = (details ?? []).Select(d => new { code, message = d, severity = "error" }).ToList(),
        };
        foreach (var (k, v) in facts ?? new Dictionary<string, object>()) inner[k] = v;
        return Results.Json(new
        {
            error = new
            {
                code,
                message = new { lang = "en", value = message },
                innererror = inner,
            },
        }, statusCode: status);
    }
}
