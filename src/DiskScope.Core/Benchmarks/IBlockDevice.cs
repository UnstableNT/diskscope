namespace DiskScope.Core.Benchmarks;

/// <summary>Sector-aligned, read-only block access used by the measurement engines.</summary>
internal interface IBlockDevice : IDisposable
{
    ulong Size { get; }

    uint SectorSize { get; }

    /// <summary>Reads <paramref name="length"/> bytes at <paramref name="offset"/> into the device's own buffer.</summary>
    /// <exception cref="System.IO.IOException">The device reported a read error.</exception>
    int Read(ulong offset, int length);
}
