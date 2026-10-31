using System.Globalization;

namespace DiskScope.Core.Benchmarks;

/// <summary>A byte range of a drive ("Bereich", SPEC 4.1). End is exclusive.</summary>
public readonly record struct DriveRange(ulong Start, ulong End)
{
    public ulong Length => End - Start;

    public static DriveRange Full(ulong size) => new(0, size);

    public static DriveRange FromPercent(ulong size, double startPercent, double endPercent)
    {
        if (startPercent < 0 || endPercent > 100 || startPercent >= endPercent)
        {
            throw new ArgumentException($"Ungültiger Bereich {startPercent}–{endPercent} %.");
        }

        return new DriveRange((ulong)(size * startPercent / 100), (ulong)(size * endPercent / 100));
    }

    /// <summary>
    /// Parses "0-100" (percent), "10%-50%" or "10GB-200GB" (decimal gigabytes).
    /// </summary>
    public static DriveRange Parse(string text, ulong size)
    {
        var parts = text.Split('-', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            throw new ArgumentException($"Ungültiger Bereich „{text}“ (Beispiele: 0-100, 10GB-200GB).");
        }

        if (parts.All(p => p.EndsWith("GB", StringComparison.OrdinalIgnoreCase)))
        {
            var start = (ulong)(ParseNumber(parts[0][..^2]) * 1e9);
            var end = Math.Min(size, (ulong)(ParseNumber(parts[1][..^2]) * 1e9));
            return start < end ? new DriveRange(start, end) : throw new ArgumentException($"Ungültiger Bereich „{text}“.");
        }

        return FromPercent(size, ParseNumber(parts[0].TrimEnd('%')), ParseNumber(parts[1].TrimEnd('%')));
    }

    /// <summary>Clamps the range to whole sectors inside the drive.</summary>
    public DriveRange AlignTo(uint sectorSize, ulong size)
    {
        var start = Start / sectorSize * sectorSize;
        var end = Math.Min(End, size) / sectorSize * sectorSize;
        return end > start ? new DriveRange(start, end) : throw new ArgumentException("Der Bereich ist kleiner als ein Sektor.");
    }

    private static double ParseNumber(string text) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new ArgumentException($"Ungültige Zahl „{text}“.");
}

public static class BlockSize
{
    public const int Min = 512;
    public const int Max = 8 * 1024 * 1024;

    /// <summary>Parses "512", "4K", "64KiB", "1M", "8MB" (binary units).</summary>
    public static int Parse(string text)
    {
        var t = text.Trim().ToUpperInvariant().Replace("IB", string.Empty, StringComparison.Ordinal).TrimEnd('B');
        long factor = 1;
        if (t.EndsWith('K'))
        {
            factor = 1024;
            t = t[..^1];
        }
        else if (t.EndsWith('M'))
        {
            factor = 1024 * 1024;
            t = t[..^1];
        }

        if (!long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            throw new ArgumentException($"Ungültige Blockgröße „{text}“ (Beispiele: 512, 4K, 64K, 1M).");
        }

        return Validate(value * factor);
    }

    public static int Validate(long bytes) =>
        bytes is >= Min and <= Max && (bytes & (bytes - 1)) == 0
            ? (int)bytes
            : throw new ArgumentException($"Blockgröße muss eine Zweierpotenz zwischen 512 B und 8 MiB sein, nicht {bytes} B.");
}
