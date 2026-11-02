namespace Mocks.SapApi;

/// <summary>SAP Gateway style error body: { "error": { "code", "message": { "lang", "value" } } }.</summary>
public static class ODataError
{
    public static IResult Create(int status, string code, string message, IEnumerable<string>? details = null) =>
        Results.Json(new
        {
            error = new
            {
                code,
                message = new { lang = "en", value = message },
                innererror = new { errordetails = (details ?? []).Select(d => new { code, message = d, severity = "error" }) },
            },
        }, statusCode: status);
}
