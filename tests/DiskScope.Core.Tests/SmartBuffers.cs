using System.Buffers.Binary;

namespace DiskScope.Core.Tests;

/// <summary>Builds synthetic SMART READ DATA / THRESHOLDS sectors and NVMe health logs.</summary>
internal static class SmartBuffers
{
    public static byte[] AtaData(params (byte Id, byte Current, byte Worst, ulong Raw)[] entries)
    {
        var data = new byte[512];
        BinaryPrimitives.WriteUInt16LittleEndian(data, 0x0010);
        for (var i = 0; i < entries.Length; i++)
        {
            var offset = 2 + (i * 12);
            data[offset] = entries[i].Id;
            data[offset + 1] = 0x03; // flags: pre-failure, online
            data[offset + 3] = entries[i].Current;
            data[offset + 4] = entries[i].Worst;
            for (var b = 0; b < 6; b++)
            {
                data[offset + 5 + b] = (byte)(entries[i].Raw >> (8 * b));
            }
        }

        FixChecksum(data);
        return data;
    }

    public static byte[] AtaThresholds(params (byte Id, byte Threshold)[] entries)
    {
        var data = new byte[512];
        BinaryPrimitives.WriteUInt16LittleEndian(data, 0x0010);
        for (var i = 0; i < entries.Length; i++)
        {
            data[2 + (i * 12)] = entries[i].Id;
            data[2 + (i * 12) + 1] = entries[i].Threshold;
        }

        FixChecksum(data);
        return data;
    }

    public static byte[] NvmeLog(
        byte criticalWarning = 0,
        ushort kelvin = 310,
        byte spare = 100,
        byte spareThreshold = 10,
        byte used = 3,
        ulong unitsRead = 1000,
        ulong unitsWritten = 2000,
        ulong powerOnHours = 1234,
        ulong mediaErrors = 0)
    {
        var log = new byte[512];
        log[0] = criticalWarning;
        BinaryPrimitives.WriteUInt16LittleEndian(log.AsSpan(1), kelvin);
        log[3] = spare;
        log[4] = spareThreshold;
        log[5] = used;
        BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(32), unitsRead);
        BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(48), unitsWritten);
        BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(112), 42);
        BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(128), powerOnHours);
        BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(160), mediaErrors);
        return log;
    }

    private static void FixChecksum(byte[] data)
    {
        byte sum = 0;
        for (var i = 0; i < 511; i++)
        {
            sum += data[i];
        }

        data[511] = (byte)(0x100 - sum);
    }
}
