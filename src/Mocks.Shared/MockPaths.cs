using Microsoft.AspNetCore.Http;

namespace Mocks.Shared;

internal static class MockPaths
{
    /// <summary>Admin, health and documentation endpoints are never faulted or rate limited.</summary>
    public static bool IsInfrastructure(PathString path) =>
        path.StartsWithSegments("/_admin")
        || path.StartsWithSegments("/health")
        || path.StartsWithSegments("/openapi")
        || path.StartsWithSegments("/scalar");
}
