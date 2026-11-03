using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Procurement.AssistantApi.Model;

namespace Procurement.AssistantApi.Infrastructure;

/// <summary>
/// Proof that the user saw and accepted exactly this request. Validate returns a token bound to the user and the
/// request content; create only accepts the request with that token. An assistant that changes anything after the
/// user said yes (a quantity, a material, the cost center) gets CONFIRMATION_MISMATCH instead of a requisition.
/// </summary>
public static class Confirmations
{
    public const string Header = "X-User-Confirmation";

    private static readonly JsonSerializerOptions Canonical = new(JsonSerializerDefaults.Web);

    public static string Issue(AuthOptions auth, string user, CreateRequisitionRequest request)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(auth.SigningKey));
        var content = user + "\n" + JsonSerializer.Serialize(request, Canonical);
        return "uc1." + Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(content))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool Matches(AuthOptions auth, string user, CreateRequisitionRequest request, string token) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Issue(auth, user, request)), Encoding.UTF8.GetBytes(token));
}
