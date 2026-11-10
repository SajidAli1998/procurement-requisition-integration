using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Procurement.Contracts;

namespace Procurement.Sync.Mapping;

public static class Fingerprints
{
    private static readonly JsonSerializerOptions Canonical = new(JsonSerializerDefaults.Web);

    // Version is excluded so the bump caused by our own status write-back does not look like a change.
    public static string ContentHash(Requisition r) =>
        Sha256(JsonSerializer.Serialize(r with { Version = 0 }, Canonical));

    // Deterministic per (requisition, version), so a retried create reuses the same key.
    public static string CreateKey(string sourceId, long version) => "procsync-" + Sha256($"create:{sourceId}:{version}")[..32];

    private static string Sha256(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}
