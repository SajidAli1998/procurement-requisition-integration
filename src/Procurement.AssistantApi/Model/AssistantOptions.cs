namespace Procurement.AssistantApi.Model;

public sealed class AssistantOptions
{
    public const string Section = "Assistant";

    public string SourceBaseUrl { get; set; } = "http://localhost:5101/";

    // Anything bigger has to be raised by a person.
    public decimal MaxRequestAmount { get; set; } = 25_000m;

    public int MaxLines { get; set; } = 20;
    public decimal MaxQuantityPerLine { get; set; } = 1_000m;
    public int MaxNeededByDays { get; set; } = 365;
    public int RequestsPerMinute { get; set; } = 60;
    public TimeSpan IdempotencyRetention { get; set; } = TimeSpan.FromHours(24);

    // The user's "yes" should not stay valid indefinitely.
    public TimeSpan ConfirmationLifetime { get; set; } = TimeSpan.FromMinutes(15);
}

public sealed class AuthOptions
{
    public const string Section = "Auth";
    public string Issuer { get; set; } = "procurement-dev";
    public string Audience { get; set; } = "procurement-assistant-api";

    // Development only. Production validates against the identity provider's keys.
    public string SigningKey { get; set; } = "";
}

public static class Scopes
{
    public const string Draft = "requisitions:draft";
    public const string Read = "requisitions:read";
    public const string ReadAll = "requisitions:read:all";
}
