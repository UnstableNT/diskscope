using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace DiskScope.Core.Native;

/// <summary>One piece of a volume on a physical disk (VOLUME_DISK_EXTENTS entry).</summary>
internal readonly record struct VolumeExtent(int DiskNumber, ulong StartingOffset, ulong Length);

/// <summary>Raw facts about a volume as reported by the volume manager and file system.</summary>
internal sealed record RawVolume(
    string VolumeName,
    string? DeviceName,
    IReadOnlyList<string> MountPoints,
    IReadOnlyList<VolumeExtent> Extents,
    string Label,
    string FileSystem,
    uint? SerialNumber,
    ulong? TotalBytes,
    ulong? FreeBytes);

internal static class Volumes
{
    private const int ErrorNoMoreFiles = 18;
    private const int VolumeNameLength = 50;
    private const int MaxExtents = 32;

    public static unsafe IReadOnlyList<RawVolume> Enumerate()
    {
        var result = new List<RawVolume>();
        Span<char> name = stackalloc char[VolumeNameLength + 1];

        using var find = PInvoke.FindFirstVolume(name);
        if (find.IsInvalid)
        {
            throw new DiskScopeException(nameof(PInvoke.FindFirstVolume), Marshal.GetLastPInvokeError());
        }

        while (true)
        {
            result.Add(Describe(TerminatedString(name)));

            if (!PInvoke.FindNextVolume((HANDLE)find.DangerousGetHandle(), name))
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == ErrorNoMoreFiles)
                {
                    break;
                }

                throw new DiskScopeException(nameof(PInvoke.FindNextVolume), error);
            }
        }

        return result;
    }

    internal static IReadOnlyList<VolumeExtent> ParseExtents(ReadOnlySpan<byte> buffer)
    {
        // VOLUME_DISK_EXTENTS: NumberOfDiskExtents (4), padding (4), DISK_EXTENT[] of 24 bytes each
        // DISK_EXTENT: DiskNumber (4), padding (4), StartingOffset (8), ExtentLength (8)
        if (buffer.Length < 8)
        {
            return [];
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(buffer);
        var extents = new List<VolumeExtent>(count);
        for (var i = 0; i < count; i++)
        {
            var entry = 8 + (i * 24);
            if (entry + 24 > buffer.Length)
            {
                break;
            }

            extents.Add(new VolumeExtent(
                BinaryPrimitives.ReadInt32LittleEndian(buffer[entry..]),
                BinaryPrimitives.ReadUInt64LittleEndian(buffer[(entry + 8)..]),
                BinaryPrimitives.ReadUInt64LittleEndian(buffer[(entry + 16)..])));
        }

        return extents;
    }

    private static RawVolume Describe(string volumeName)
    {
        // volumeName has the form \\?\Volume{guid}\
        var mountPoints = GetMountPoints(volumeName);
        var extents = GetExtents(volumeName.TrimEnd('\\'));
        var deviceName = QueryDosDeviceName(volumeName);

        Span<char> label = stackalloc char[261];
        Span<char> fileSystem = stackalloc char[261];
        string labelText = string.Empty, fileSystemText = string.Empty;
        uint? serial = null;
        if (PInvoke.GetVolumeInformation(volumeName, label, out var serialNumber, out _, out _, fileSystem))
        {
            labelText = TerminatedString(label);
            fileSystemText = TerminatedString(fileSystem);
            serial = serialNumber;
        }

        ulong? total = null, free = null;
        if (PInvoke.GetDiskFreeSpaceEx(volumeName, out _, out var totalBytes, out var freeBytes))
        {
            total = totalBytes;
            free = freeBytes;
        }

        return new RawVolume(volumeName, deviceName, mountPoints, extents, labelText, fileSystemText, serial, total, free);
    }

    private static unsafe string[] GetMountPoints(string volumeName)
    {
        var buffer = new char[1024];
        fixed (char* p = buffer)
        {
            if (!PInvoke.GetVolumePathNamesForVolumeName(volumeName, new PZZWSTR(p), (uint)buffer.Length, out _))
            {
                return [];
            }
        }

        return DeviceTree.SplitMultiString(buffer).ToArray();
    }

    private static IReadOnlyList<VolumeExtent> GetExtents(string volumeDevicePath)
    {
        try
        {
            using var handle = DeviceIo.Open(volumeDevicePath, DeviceAccess.Query);
            var output = new byte[8 + (MaxExtents * 24)];
            return DeviceIo.TryControl(handle, PInvoke.IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, default, output, out var bytes, out _)
                ? ParseExtents(output.AsSpan(0, bytes))
                : [];
        }
        catch (DiskScopeException)
        {
            return [];
        }
    }

    private static string? QueryDosDeviceName(string volumeName)
    {
        // QueryDosDevice expects "Volume{guid}" without the \\?\ prefix and trailing backslash.
        const string prefix = @"\\?\";
        var dosName = volumeName.StartsWith(prefix, StringComparison.Ordinal) ? volumeName[prefix.Length..] : volumeName;
        dosName = dosName.TrimEnd('\\');

        Span<char> target = stackalloc char[512];
        return PInvoke.QueryDosDevice(dosName, target) == 0 ? null : TerminatedString(target);
    }

    private static string TerminatedString(ReadOnlySpan<char> buffer)
    {
        var end = buffer.IndexOf('\0');
        return new string(end < 0 ? buffer : buffer[..end]);
    }
}
