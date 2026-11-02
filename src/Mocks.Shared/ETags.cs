using Microsoft.AspNetCore.Http;

namespace Mocks.Shared;

public enum PreconditionResult { Ok, Missing, Failed }

/// <summary>Strong ETags derived from a monotonically increasing entity version.</summary>
public static class ETags
{
    public static string For(long version) => $"\"{version}\"";

    public static PreconditionResult CheckIfMatch(HttpRequest request, long currentVersion)
    {
        var header = request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(header)) return PreconditionResult.Missing;
        if (header.Trim() == "*") return PreconditionResult.Ok;

        var expected = For(currentVersion);
        foreach (var candidate in header.Split(',', StringSplitOptions.TrimEntries))
        {
            // If-Match uses strong comparison (RFC 9110 §13.1.1); weak tags never match.
            if (candidate == expected) return PreconditionResult.Ok;
        }
        return PreconditionResult.Failed;
    }

    public static IResult PreconditionProblem(PreconditionResult result, long currentVersion, HttpResponse response)
    {
        response.Headers.ETag = For(currentVersion);
        return result == PreconditionResult.Missing
            ? Results.Problem(
                statusCode: StatusCodes.Status428PreconditionRequired,
                title: "If-Match header is required",
                detail: "Fetch the resource and send its ETag in If-Match to update it.")
            : Results.Problem(
                statusCode: StatusCodes.Status412PreconditionFailed,
                title: "Version conflict",
                detail: $"The resource has changed. Current ETag is {For(currentVersion)}.");
    }
}
