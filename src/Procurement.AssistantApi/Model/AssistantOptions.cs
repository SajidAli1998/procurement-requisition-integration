namespace Procurement.AssistantApi.Model;

public sealed class AssistantOptions
{
    public const string Section = "Assistant";

    public string SourceBaseUrl { get; set; } = "http://localhost:5101/";

    /// <summary>Largest total a single assistant-raised request may have; bigger ones need a person.</summary>
    public decimal MaxRequestAmount { get; set; } = 25_000m;

    public int MaxLines { get; set; } = 20;
    public decimal MaxQuantityPerLine { get; set; } = 1_000m;
    public int MaxNeededByDays { get; set; } = 365;
    public int RequestsPerMinute { get; set; } = 60;
    public TimeSpan IdempotencyRetention { get; set; } = TimeSpan.FromHours(24);
}

public sealed class AuthOptions
{
    public const string Section = "Auth";
    public string Issuer { get; set; } = "procurement-dev";
    public string Audience { get; set; } = "procurement-assistant-api";

    /// <summary>Development signing key only. Production uses the identity provider's keys (e.g. Entra ID).</summary>
    public string SigningKey { get; set; } = "";
}

public static class Scopes
{
    public const string Draft = "requisitions:draft";
    public const string Read = "requisitions:read";
}
