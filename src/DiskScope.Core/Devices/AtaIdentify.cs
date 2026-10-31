using System.Buffers.Binary;
using System.Text;

namespace DiskScope.Core.Devices;

/// <summary>Fields of the ATA IDENTIFY DEVICE data (512 bytes, 256 little-endian words; ATA/ACS).</summary>
public sealed record AtaIdentifyData(
    string Model,
    string Serial,
    string Firmware,
    ulong? BufferBytes,
    ushort RotationRate,
    int? CurrentSataGeneration,
    int? MaxSataGeneration,
    IReadOnlyList<Feature> Features,
    int AamLevel = 0,
    int AamRecommended = 0,
    int ApmLevel = 0)
{
    /// <summary>Formats the SATA link, e.g. "SATA 3 (6.0 Gb/s)".</summary>
    public string? Link
    {
        get
        {
            if (CurrentSataGeneration is { } current)
            {
                var text = $"SATA {current} ({SataSpeed(current)})";
                return MaxSataGeneration is { } max && max > current ? $"{text}, max. SATA {max}" : text;
            }

            return MaxSataGeneration is { } onlyMax ? $"SATA {onlyMax} (bis {SataSpeed(onlyMax)})" : null;
        }
    }

    private static string SataSpeed(int generation) => generation switch
    {
        1 => "1.5 Gb/s",
        2 => "3.0 Gb/s",
        _ => "6.0 Gb/s",
    };
}

public static class AtaIdentify
{
    public const int Length = 512;

    public static AtaIdentifyData Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < Length)
        {
            throw new ArgumentException($"IDENTIFY-Daten müssen {Length} Byte lang sein.", nameof(data));
        }

        // Word 21 (retired in ACS) holds the buffer size in 512-byte units on older drives.
        var bufferWords = Word(data, 21);
        ulong? buffer = bufferWords is 0 or 0xFFFF ? null : bufferWords * 512UL;

        return new AtaIdentifyData(
            Model: String(data, 27, 46),
            Serial: String(data, 10, 19),
            Firmware: String(data, 23, 26),
            BufferBytes: buffer,
            RotationRate: Word(data, 217),
            CurrentSataGeneration: CurrentGeneration(data),
            MaxSataGeneration: MaxGeneration(data),
            Features: ParseFeatures(data),
            AamLevel: Word(data, 94) & 0xFF,
            AamRecommended: Word(data, 94) >> 8,
            ApmLevel: Word(data, 91) & 0xFF);
    }

    internal static ushort Word(ReadOnlySpan<byte> data, int index) =>
        BinaryPrimitives.ReadUInt16LittleEndian(data[(index * 2)..]);

    /// <summary>ATA strings store two characters per word with the first character in the high byte.</summary>
    internal static string String(ReadOnlySpan<byte> data, int firstWord, int lastWord)
    {
        var builder = new StringBuilder((lastWord - firstWord + 1) * 2);
        for (var word = firstWord; word <= lastWord; word++)
        {
            var value = Word(data, word);
            builder.Append(ToPrintable((byte)(value >> 8)));
            builder.Append(ToPrintable((byte)value));
        }

        return builder.ToString().Trim(' ', '\0');
    }

    private static char ToPrintable(byte b) => b is >= 0x20 and < 0x7F ? (char)b : ' ';

    private static bool IsValidWord(ushort value) => value is not (0 or 0xFFFF);

    private static int? CurrentGeneration(ReadOnlySpan<byte> data)
    {
        // Word 77 bits 3:1: coded value of the currently negotiated SATA signal speed.
        var word77 = Word(data, 77);
        if (!IsValidWord(word77))
        {
            return null;
        }

        var code = (word77 >> 1) & 0x7;
        return code is >= 1 and <= 3 ? code : null;
    }

    private static int? MaxGeneration(ReadOnlySpan<byte> data)
    {
        // Word 76 bits 1..3: Gen1/Gen2/Gen3 signaling speeds supported.
        var word76 = Word(data, 76);
        if (!IsValidWord(word76))
        {
            return null;
        }

        for (var generation = 3; generation >= 1; generation--)
        {
            if ((word76 & (1 << generation)) != 0)
            {
                return generation;
            }
        }

        return null;
    }

    private static IReadOnlyList<Feature> ParseFeatures(ReadOnlySpan<byte> data)
    {
        // Words 82-84 are valid if word 83 bits 15:14 = 01b; words 85-87 if word 87 bits 15:14 = 01b.
        var supportedValid = (Word(data, 83) & 0xC000) == 0x4000;
        var enabledValid = (Word(data, 87) & 0xC000) == 0x4000;

        var w82 = supportedValid ? Word(data, 82) : (ushort)0;
        var w83 = supportedValid ? Word(data, 83) : (ushort)0;
        var w85 = Word(data, 85);
        var w86 = Word(data, 86);

        var w76 = Word(data, 76);
        var w78 = Word(data, 78);
        var w79 = Word(data, 79);
        var sataValid = IsValidWord(w76);

        bool? Enabled(ushort word, int bit) => enabledValid ? Bit(word, bit) : null;

        var hipm = sataValid && Bit(w76, 9);
        var dipm = IsValidWord(w78) && Bit(w78, 3);
        bool? dipmEnabled = IsValidWord(w79) ? Bit(w79, 3) : null;

        return
        [
            new Feature(FeatureKind.Smart, Bit(w82, 0), Enabled(w85, 0)),
            new Feature(FeatureKind.FirmwareUpdate, Bit(w83, 0), null),
            new Feature(FeatureKind.PowerManagement, Bit(w82, 3), Enabled(w85, 3)),
            new Feature(FeatureKind.AdvancedPowerManagement, Bit(w83, 3), Enabled(w86, 3)),
            new Feature(FeatureKind.InterfacePowerManagement, hipm || dipm, dipm ? dipmEnabled : null),
            new Feature(FeatureKind.Lba48, Bit(w83, 10), Enabled(w86, 10)),
            new Feature(FeatureKind.Ncq, sataValid && Bit(w76, 8), null),
            new Feature(FeatureKind.Trim, Bit(Word(data, 169), 0), null),
            new Feature(FeatureKind.AutomaticAcousticManagement, Bit(w83, 9), Enabled(w86, 9)),
            new Feature(FeatureKind.PowerUpInStandby, Bit(w83, 5), Enabled(w86, 5)),
            new Feature(FeatureKind.ReadLookAhead, Bit(w82, 6), Enabled(w85, 6)),
            new Feature(FeatureKind.WriteCache, Bit(w82, 5), Enabled(w85, 5)),
            new Feature(FeatureKind.Sct, Bit(Word(data, 206), 0), null),
            new Feature(FeatureKind.HostProtectedArea, Bit(w82, 10), Enabled(w85, 10)),
            new Feature(FeatureKind.DeviceConfigurationOverlay, Bit(w83, 11), null),
            new Feature(FeatureKind.Security, Bit(w82, 1), Enabled(w85, 1)),
        ];
    }

    private static bool Bit(ushort word, int bit) => (word & (1 << bit)) != 0;
}
