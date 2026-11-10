using System.Buffers.Text;
using System.Text;

namespace Mocks.Shared;

public readonly record struct KeysetCursor(DateTime Timestamp, string Key)
{
    public string Encode() =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{Timestamp.Ticks}:{Key}"));

    public static bool TryDecode(string? token, out KeysetCursor cursor)
    {
        cursor = default;
        if (string.IsNullOrEmpty(token)) return false;
        try
        {
            var raw = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(token));
            var sep = raw.IndexOf(':');
            if (sep <= 0 || !long.TryParse(raw.AsSpan(0, sep), out var ticks)) return false;
            cursor = new KeysetCursor(new DateTime(ticks, DateTimeKind.Utc), raw[(sep + 1)..]);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>True when (timestamp, key) sorts strictly after this cursor.</summary>
    public bool IsBefore(DateTime timestamp, string key) =>
        timestamp > Timestamp || (timestamp == Timestamp && string.CompareOrdinal(key, Key) > 0);
}
