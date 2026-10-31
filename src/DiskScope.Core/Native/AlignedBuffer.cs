using System.Buffers;
using System.Runtime.InteropServices;

namespace DiskScope.Core.Native;

/// <summary>
/// Page-aligned native memory exposed as <see cref="Memory{T}"/>, for unbuffered async file I/O
/// (FILE_FLAG_NO_BUFFERING requires sector-aligned buffers).
/// </summary>
internal sealed unsafe class AlignedBuffer : MemoryManager<byte>
{
    private const nuint PageSize = 4096;

    private byte* _pointer;

    public AlignedBuffer(int length)
    {
        Length = length;
        _pointer = (byte*)NativeMemory.AlignedAlloc((nuint)length, PageSize);
        new Span<byte>(_pointer, length).Clear();
    }

    public int Length { get; }

    public override Span<byte> GetSpan() => new(_pointer, Length);

    public override MemoryHandle Pin(int elementIndex = 0) => new(_pointer + elementIndex);

    public override void Unpin()
    {
    }

    protected override void Dispose(bool disposing)
    {
        if (_pointer != null)
        {
            NativeMemory.AlignedFree(_pointer);
            _pointer = null;
        }
    }
}

/// <summary>Volume geometry needed for unbuffered file I/O.</summary>
internal static class VolumeGeometry
{
    /// <summary>Bytes per sector of the volume containing <paramref name="path"/>, 4096 if unknown.</summary>
    public static uint SectorSize(string path)
    {
        var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path));
        return root is not null && Windows.Win32.PInvoke.GetDiskFreeSpace(root, out _, out var bytesPerSector, out _, out _)
            ? Math.Max(512u, bytesPerSector)
            : 4096u;
    }
}
