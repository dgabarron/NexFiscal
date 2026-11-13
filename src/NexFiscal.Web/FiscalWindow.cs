using System.Globalization;
using System.Text.Json;

/// <summary>Tratamento UTC compartilhado para as janelas fiscais NF-e e CT-e.</summary>
public static class FiscalWindow
{
    public static DateTimeOffset? NextAllowedUtc(JsonElement root)
    {
        if (!root.TryGetProperty("nextAllowedUtc", out var next) ||
            next.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(next.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value))
            return null;
        return value.ToUniversalTime();
    }

    public static bool IsWaiting(JsonElement root, DateTimeOffset nowUtc)
    {
        var next = NextAllowedUtc(root);
        return next.HasValue && next.Value > nowUtc;
    }
}
