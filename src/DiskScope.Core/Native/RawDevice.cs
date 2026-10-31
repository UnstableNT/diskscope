using System.Runtime.InteropServices;
using DiskScope.Core.Benchmarks;
using Microsoft.Win32.SafeHandles;
using Windows.Win32.Storage.FileSystem;

namespace DiskScope.Core.Native;

/// <summary>
/// Read-only, unbuffered access to a physical drive (GENERIC_READ + FILE_FLAG_NO_BUFFERING)
/// with one page-aligned buffer that is reused for every read (SPEC 2, 4.4).
/// </summary>
internal sealed unsafe class RawDevice : IBlockDevice
{
    private const nuint PageSize = 4096;

    private readonly SafeFileHandle _handle;
    private readonly byte* _buffer;
    private readonly int _capacity;

    private RawDevice(SafeFileHandle handle, int capacity, ulong size, uint sectorSize)
    {
        _handle = handle;
        _capacity = capacity;
        _buffer = (byte*)NativeMemory.AlignedAlloc((nuint)capacity, PageSize);
        Size = size;
        SectorSize = sectorSize;
    }

    public ulong Size { get; }

    public uint SectorSize { get; }

    public static RawDevice Open(string devicePath, ulong size, uint sectorSize, int bufferCapacity)
    {
        var handle = DeviceIo.Open(devicePath, DeviceAccess.Read, FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_NO_BUFFERING);
        var capacity = (int)(((uint)bufferCapacity + (uint)PageSize - 1) & ~((uint)PageSize - 1));
        return new RawDevice(handle, capacity, size, sectorSize);
    }

    public int Read(ulong offset, int length)
    {
        if ((uint)length > (uint)_capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        return System.IO.RandomAccess.Read(_handle, new Span<byte>(_buffer, length), (long)offset);
    }

    public void Dispose()
    {
        _handle.Dispose();
        NativeMemory.AlignedFree(_buffer);
    }
}

/// <summary>System-wide CPU load from GetSystemTimes.</summary>
internal static class CpuTimes
{
    public static (ulong Idle, ulong Total) Sample()
    {
        Windows.Win32.PInvoke.GetSystemTimes(out var idle, out var kernel, out var user);
        static ulong ToUInt64(System.Runtime.InteropServices.ComTypes.FILETIME t) =>
            ((ulong)(uint)t.dwHighDateTime << 32) | (uint)t.dwLowDateTime;

        // Kernel time includes idle time.
        return (ToUInt64(idle), ToUInt64(kernel) + ToUInt64(user));
    }

    public static double LoadPercent((ulong Idle, ulong Total) start, (ulong Idle, ulong Total) end)
    {
        var total = end.Total - start.Total;
        return total == 0 ? 0 : Math.Clamp(100.0 * (1.0 - ((double)(end.Idle - start.Idle) / total)), 0, 100);
    }
}
