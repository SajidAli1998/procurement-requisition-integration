using Procurement.AssistantApi.Model;

namespace Procurement.AssistantApi.Infrastructure;

// Agents branch on these codes, never on the title, so they must not change.
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
    public const string NotYourRequisition = "NOT_YOUR_REQUISITION";
    public const string ConfirmationRequired = "CONFIRMATION_REQUIRED";
    public const string ConfirmationMismatch = "CONFIRMATION_MISMATCH";
    public const string ConfirmationExpired = "CONFIRMATION_EXPIRED";
    public const string PayloadTooLarge = "PAYLOAD_TOO_LARGE";
    public const string ConfirmationAlreadyUsed = "CONFIRMATION_ALREADY_USED";
}

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
