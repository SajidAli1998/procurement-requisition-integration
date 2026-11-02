using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Procurement.Contracts;

namespace Procurement.Sync.Mapping;

public static class Fingerprints
{
    private static readonly JsonSerializerOptions Canonical = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Hash of the business content only (version excluded), so a version bump caused by our own
    /// status write-back is recognised as "unchanged" and not re-sent.
    /// </summary>
    public static string ContentHash(Requisition r) =>
        Sha256(JsonSerializer.Serialize(r with { Version = 0 }, Canonical));

    /// <summary>Deterministic per (requisition, version): a retry of the same create always reuses the same key.</summary>
    public static string CreateKey(string sourceId, long version) => "procsync-" + Sha256($"create:{sourceId}:{version}")[..32];

    private static string Sha256(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}
