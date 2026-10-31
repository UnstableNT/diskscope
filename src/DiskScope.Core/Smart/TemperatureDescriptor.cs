using System.Buffers.Binary;

namespace DiskScope.Core.Smart;

/// <summary>
/// Parses STORAGE_TEMPERATURE_DATA_DESCRIPTOR (StorageDeviceTemperatureProperty):
/// Version, Size, CriticalTemperature (short, 8), WarningTemperature (short, 10), InfoCount (ushort, 12),
/// reserved up to 24, then STORAGE_TEMPERATURE_INFO[InfoCount] of 16 bytes with Temperature (short) at 2. All °C.
/// </summary>
public static class TemperatureDescriptor
{
    private const int InfoOffset = 24;
    private const int InfoSize = 16;

    public static (int? Current, int? Warning, int? Critical)? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < InfoOffset + InfoSize)
        {
            return null;
        }

        var critical = BinaryPrimitives.ReadInt16LittleEndian(data[8..]);
        var warning = BinaryPrimitives.ReadInt16LittleEndian(data[10..]);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(data[12..]);
        if (count == 0)
        {
            return null;
        }

        // Index 0 is the device's composite / overall temperature.
        var current = BinaryPrimitives.ReadInt16LittleEndian(data[(InfoOffset + 2)..]);
        return (Plausible(current), Plausible(warning), Plausible(critical));
    }

    private static int? Plausible(short celsius) => celsius is > -40 and < 150 && celsius != 0 ? celsius : null;
}
