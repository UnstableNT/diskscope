using System.Globalization;

namespace DiskScope.Core;

public static class ByteSize
{
    private static readonly string[] DecimalUnits = ["B", "KB", "MB", "GB", "TB", "PB"];
    private static readonly string[] BinaryUnits = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];

    /// <summary>Decimal units as drive manufacturers use them (1 GB = 10^9 bytes).</summary>
    public static string Format(ulong bytes, IFormatProvider? provider = null) =>
        Format(bytes, 1000, DecimalUnits, provider);

    /// <summary>Binary units (1 KiB = 1024 bytes), used for block and buffer sizes.</summary>
    public static string FormatBinary(ulong bytes, IFormatProvider? provider = null) =>
        Format(bytes, 1024, BinaryUnits, provider);

    /// <summary>Parses sizes like "500M", "1G", "64KiB", "4096" with binary units.</summary>
    public static long ParseBinary(string text)
    {
        var t = text.Trim().ToUpperInvariant().Replace("IB", string.Empty, StringComparison.Ordinal).TrimEnd('B');
        var factor = t.Length > 0 ? t[^1] switch
        {
            'K' => 1L << 10,
            'M' => 1L << 20,
            'G' => 1L << 30,
            'T' => 1L << 40,
            _ => 1L,
        } : 1L;
        if (factor > 1)
        {
            t = t[..^1];
        }

        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0
            ? (long)(value * factor)
            : throw new ArgumentException($"Ungültige Größe „{text}“ (Beispiele: 500M, 1G).");
    }

    private static string Format(ulong bytes, int step, string[] units, IFormatProvider? provider)
    {
        provider ??= CultureInfo.InvariantCulture;
        double value = bytes;
        var unit = 0;
        while (value >= step && unit < units.Length - 1)
        {
            value /= step;
            unit++;
        }

        return string.Create(provider, $"{value:0.#} {units[unit]}");
    }
}
