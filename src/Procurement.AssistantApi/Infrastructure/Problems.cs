using Procurement.AssistantApi.Model;

namespace Procurement.AssistantApi.Infrastructure;

/// <summary>Stable error codes. An agent should branch on these, never on the human-readable title.</summary>
public static class ErrorCodes
{
    public const string IdempotencyKeyRequired = "IDEMPOTENCY_KEY_REQUIRED";
    public const string IdempotencyKeyReused = "IDEMPOTENCY_KEY_REUSED";
    public const string RequestInProgress = "REQUEST_IN_PROGRESS";
    public const string InvalidRequestBody = "INVALID_REQUEST_BODY";
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string NotFound = "NOT_FOUND";
    public const string PreconditionRequired = "PRECONDITION_REQUIRED";
    public const string VersionConflict = "VERSION_CONFLICT";
    public const string NotWithdrawable = "NOT_WITHDRAWABLE";
    public const string UpstreamUnavailable = "UPSTREAM_UNAVAILABLE";
    public const string RateLimited = "RATE_LIMITED";
    public const string Unauthorized = "UNAUTHORIZED";
    public const string Forbidden = "FORBIDDEN";
}

/// <summary>RFC 9457 problem details with extensions an agent can act on.</summary>
public static class Problems
{
    public static IResult Create(int status, string code, string title, string? detail = null, bool retryable = false,
        string? suggestedAction = null, IReadOnlyList<ValidationIssue>? invalidFields = null, int? retryAfterSeconds = null)
    {
        var extensions = new Dictionary<string, object?>
        {
            ["code"] = code,
            ["retryable"] = retryable,
        };
        if (suggestedAction is not null) extensions["suggestedAction"] = suggestedAction;
        if (invalidFields is not null) extensions["invalidFields"] = invalidFields;
        if (retryAfterSeconds is not null) extensions["retryAfter"] = retryAfterSeconds;
        return TypedResults.Problem(detail: detail, statusCode: status, title: title, type: $"urn:procurement:error:{code.ToLowerInvariant()}", extensions: extensions);
    }
}
