using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Procurement.AssistantApi.Model;

namespace Procurement.AssistantApi.Infrastructure;

public enum ConfirmationCheck { Valid, Mismatch, Expired }

// Proof that the user accepted exactly this request. The token is an HMAC over the user, the request content
// and the issue time, so changing anything after the user said yes no longer matches.
public static class Confirmations
{
    public const string Header = "X-User-Confirmation";
    private const string Prefix = "uc2.";

    private static readonly JsonSerializerOptions Canonical = new(JsonSerializerDefaults.Web);

    public static string Issue(AuthOptions auth, string user, CreateRequisitionRequest request, DateTimeOffset now)
    {
        var issued = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return Prefix + issued + "." + Mac(auth, user, issued, request);
    }

    public static ConfirmationCheck Check(AuthOptions auth, string user, CreateRequisitionRequest request, string token, DateTimeOffset now, TimeSpan lifetime)
    {
        var parts = token.Split('.');
        if (parts.Length != 3 || parts[0] + "." != Prefix || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var issued))
            return ConfirmationCheck.Mismatch;
        var expected = Mac(auth, user, parts[1], request);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(parts[2])))
            return ConfirmationCheck.Mismatch;
        var age = now - DateTimeOffset.FromUnixTimeSeconds(issued);
        return age > lifetime || age < TimeSpan.FromMinutes(-1) ? ConfirmationCheck.Expired : ConfirmationCheck.Valid;
    }

    private static string Mac(AuthOptions auth, string user, string issued, CreateRequisitionRequest request)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(auth.SigningKey));
        var content = user + "\n" + issued + "\n" + JsonSerializer.Serialize(request, Canonical);
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(content))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

// One "yes" raises at most one requisition. Retrying the same create under its own Idempotency-Key still
// works because it replays. In memory only; several instances would need a shared store.
public sealed class ConfirmationLedger
{
    private readonly ConcurrentDictionary<string, string> _spent = new(StringComparer.Ordinal);

    public bool TrySpend(string token, string slot) => _spent.GetOrAdd(token, slot) == slot;

    public void Refund(string token, string slot) => _spent.TryRemove(new KeyValuePair<string, string>(token, slot));
}
