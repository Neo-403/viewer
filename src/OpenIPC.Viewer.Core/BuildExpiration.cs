using System;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace OpenIPC.Viewer.Core;

/// <summary>
/// Resolves the optional expiry stamped into every assembly by Directory.Build.props
/// (<c>-p:OpenIpcExpiry=…</c>). An empty stamp — the default — means the build
/// never expires. Accepted values are a number of days counted from the build
/// timestamp, or a UTC date (yyyy-MM-dd) that is the last usable day.
/// </summary>
/// <remarks>
/// A time-limited build must not be able to replace itself, so Directory.Build.props
/// also forces <c>UpdateFeed=none</c> for it. This is a convenience / trial-build
/// control, not a security boundary: the stamp is plain assembly metadata and the
/// check uses the system clock. No clock-rollback detection by design.
/// </remarks>
public static class BuildExpiration
{
    public const string ExpiryMetadataKey = "OpenIpc.Expiry";
    public const string BuiltAtMetadataKey = "OpenIpc.BuiltAt";

    private const string DateFormat = "yyyy-MM-dd";

    public static string? ConfiguredExpiry => Metadata(ExpiryMetadataKey);
    public static string? ConfiguredBuiltAt => Metadata(BuiltAtMetadataKey);

    /// <summary>True for a build stamped with a non-empty expiry.</summary>
    public static bool IsTimeLimited => !string.IsNullOrWhiteSpace(ConfiguredExpiry);

    /// <summary>UTC instant at which the build expires; null when it never expires.</summary>
    public static DateTimeOffset? ExpiresAt => Resolve(ConfiguredExpiry, ConfiguredBuiltAt);

    /// <summary>UTC expiry instant for display; null when it never expires or the stamp is broken.</summary>
    public static string? ExpiresAtText
    {
        get
        {
            var at = ExpiresAt;
            if (at is null || at == DateTimeOffset.MinValue || at == DateTimeOffset.MaxValue)
                return null;
            return at.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>False for unlimited builds; fails closed on a missing or corrupt stamp.</summary>
    public static bool IsExpired(string? expiry, string? builtAt, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(expiry)) return false;
        var at = Resolve(expiry, builtAt);
        return at is null || now >= at.Value;
    }

    public static bool IsExpired(DateTimeOffset now) => IsExpired(ConfiguredExpiry, ConfiguredBuiltAt, now);

    public static bool IsExpired() => IsExpired(DateTimeOffset.UtcNow);

    /// <summary>
    /// Returns null when the build never expires. A non-empty but malformed spec
    /// resolves to <see cref="DateTimeOffset.MinValue"/> (already expired), so a
    /// typo can never turn a time-limited build into an unlimited one.
    /// </summary>
    public static DateTimeOffset? Resolve(string? expiry, string? builtAt)
    {
        var spec = expiry?.Trim() ?? string.Empty;
        if (spec.Length == 0) return null;

        if (int.TryParse(spec, NumberStyles.None, CultureInfo.InvariantCulture, out var days) && days > 0)
        {
            if (!TryParseBuiltAt(builtAt, out var built)) return DateTimeOffset.MinValue;
            return AddDaysClamped(built, days);
        }

        if (DateTimeOffset.TryParseExact(spec, DateFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
        {
            // The given date stays usable for the whole UTC day.
            return AddDaysClamped(date, 1);
        }

        return DateTimeOffset.MinValue;
    }

    private static bool TryParseBuiltAt(string? value, out DateTimeOffset built)
    {
        built = default;
        return !string.IsNullOrWhiteSpace(value)
            && DateTimeOffset.TryParse(value!.Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out built);
    }

    private static DateTimeOffset AddDaysClamped(DateTimeOffset from, double days)
    {
        try { return from.AddDays(days); }
        catch (ArgumentOutOfRangeException) { return DateTimeOffset.MaxValue; }
    }

    private static string? Metadata(string key) =>
        typeof(BuildExpiration).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value;
}
