using DiskScope.Core.Benchmarks;

namespace DiskScope.Core.Tests;

/// <summary>In-memory block device that records reads and can fail at chosen offsets.</summary>
internal sealed class FakeBlockDevice(ulong size, uint sectorSize = 512) : IBlockDevice
{
    public ulong Size { get; } = size;

    public uint SectorSize { get; } = sectorSize;

    public List<(ulong Offset, int Length)> Reads { get; } = [];

    /// <summary>Byte offsets of unreadable sectors; any read touching one of them fails.</summary>
    public HashSet<ulong> BadOffsets { get; } = [];

    public Action? OnRead { get; set; }

    public int Read(ulong offset, int length)
    {
        offset.ShouldBeLessThanOrEqualTo(Size - (ulong)length, "Lesezugriff außerhalb des Geräts");
        (offset % SectorSize).ShouldBe(0UL, "Offset nicht sektorausgerichtet");
        (length % SectorSize).ShouldBe(0, "Länge nicht sektorausgerichtet");

        Reads.Add((offset, length));
        OnRead?.Invoke();
        if (BadOffsets.Any(bad => bad >= offset && bad < offset + (ulong)length))
        {
            throw new System.IO.IOException("Simulierter Lesefehler");
        }

        return length;
    }

    public void Dispose()
    {
    }
}
