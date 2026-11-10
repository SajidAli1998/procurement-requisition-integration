using Microsoft.AspNetCore.Http;

namespace Mocks.Shared;

internal static class MockPaths
{
    public static bool IsInfrastructure(PathString path) =>
        path.StartsWithSegments("/_admin")
        || path.StartsWithSegments("/health")
        || path.StartsWithSegments("/openapi")
        || path.StartsWithSegments("/scalar");
}
