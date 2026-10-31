using DiskScope.Core.Benchmarks;
using DiskScope.Core.Native;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;

namespace DiskScope.Core.Destructive;

/// <summary>A block device that can also write; implemented only in this namespace.</summary>
internal interface IWritableBlockDevice : IBlockDevice
{
    /// <summary>Writes <paramref name="data"/> at <paramref name="offset"/>; outside the authorized range it throws.</summary>
    void Write(ulong offset, ReadOnlySpan<byte> data);

    /// <summary>Reads back for verification and returns the data.</summary>
    ReadOnlySpan<byte> ReadBack(ulong offset, int length);
}

/// <summary>
/// An open, authorized write session: volumes of the drive are locked and dismounted (if requested) for the whole
/// session, and the raw device is opened with write access, unbuffered and write-through.
/// </summary>
public sealed class DestructiveSession : IDisposable
{
    private readonly List<SafeFileHandle> _volumeLocks;

    private DestructiveSession(WriteAuthorization authorization, List<SafeFileHandle> volumeLocks, RawWriteDevice device)
    {
        Authorization = authorization;
        _volumeLocks = volumeLocks;
        Device = device;
    }

    public WriteAuthorization Authorization { get; }

    internal IWritableBlockDevice Device { get; }

    /// <param name="bufferCapacity">Largest single transfer the session will use.</param>
    public static DestructiveSession Open(WriteAuthorization authorization, int bufferCapacity)
    {
        var locks = authorization.DismountVolumes ? LockAndDismountVolumes(authorization.Drive.Id.Number) : [];
        try
        {
            var drive = authorization.Drive;
            var device = new RawWriteDevice(
                DeviceIo.Open(drive.DevicePath, DeviceAccess.Write, FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_NO_BUFFERING | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_WRITE_THROUGH),
                drive.SizeBytes,
                drive.LogicalSectorSize,
                authorization.Range,
                bufferCapacity);
            return new DestructiveSession(authorization, locks, device);
        }
        catch
        {
            Release(locks);
            throw;
        }
    }

    public void Dispose()
    {
        Device.Dispose();
        Release(_volumeLocks);
    }

    /// <summary>FSCTL_LOCK_VOLUME + FSCTL_DISMOUNT_VOLUME for every volume on the disk (SPEC 4.7, 9.3).</summary>
    private static List<SafeFileHandle> LockAndDismountVolumes(int diskNumber)
    {
        var locks = new List<SafeFileHandle>();
        try
        {
            foreach (var volume in Volumes.Enumerate().Where(v => v.Extents.Any(e => e.DiskNumber == diskNumber)))
            {
                var handle = DeviceIo.Open(volume.VolumeName.TrimEnd('\\'), DeviceAccess.Write);
                locks.Add(handle);
                var name = volume.MountPoints.Count > 0 ? volume.MountPoints[0] : volume.VolumeName;

                // Explorer, indexing or a virus scanner often hold files open for a moment: retry the lock first.
                // If it still fails, the user asked to dismount anyway: dismounting an unlocked volume is a forced
                // dismount that invalidates all open handles, after which the lock succeeds.
                if (!TryLock(handle, attempts: 10))
                {
                    DeviceIo.Control(handle, PInvoke.FSCTL_DISMOUNT_VOLUME, default, default, "FSCTL_DISMOUNT_VOLUME");
                    if (!TryLock(handle, attempts: 5))
                    {
                        throw new WriteRefusedException($"Volume {name} kann auch nach dem Aushängen nicht gesperrt werden.");
                    }
                }

                DeviceIo.Control(handle, PInvoke.FSCTL_DISMOUNT_VOLUME, default, default, "FSCTL_DISMOUNT_VOLUME");
            }

            return locks;
        }
        catch
        {
            Release(locks);
            throw;
        }
    }

    private static bool TryLock(SafeFileHandle handle, int attempts)
    {
        for (var i = 0; i < attempts; i++)
        {
            if (DeviceIo.TryControl(handle, PInvoke.FSCTL_LOCK_VOLUME, default, default, out _, out _))
            {
                return true;
            }

            Thread.Sleep(300);
        }

        return false;
    }

    private static void Release(List<SafeFileHandle> locks)
    {
        foreach (var handle in locks)
        {
            DeviceIo.TryControl(handle, PInvoke.FSCTL_UNLOCK_VOLUME, default, default, out _, out _);
            handle.Dispose();
        }

        locks.Clear();
    }
}

/// <summary>Raw device with write access, restricted to the authorized range.</summary>
internal sealed class RawWriteDevice : IWritableBlockDevice
{
    private readonly SafeFileHandle _handle;
    private readonly AlignedBuffer _buffer;
    private readonly DriveRange _allowed;

    public RawWriteDevice(SafeFileHandle handle, ulong size, uint sectorSize, DriveRange allowed, int bufferCapacity)
    {
        _handle = handle;
        _allowed = allowed;
        Size = size;
        SectorSize = sectorSize;
        _buffer = new AlignedBuffer((int)((((uint)bufferCapacity) + 4095u) & ~4095u));
    }

    public ulong Size { get; }

    public uint SectorSize { get; }

    public int Read(ulong offset, int length) =>
        System.IO.RandomAccess.Read(_handle, _buffer.GetSpan()[..length], (long)offset);

    public ReadOnlySpan<byte> ReadBack(ulong offset, int length) => _buffer.GetSpan()[..Read(offset, length)];

    public void Write(ulong offset, ReadOnlySpan<byte> data)
    {
        if (offset < _allowed.Start || offset + (ulong)data.Length > _allowed.End)
        {
            throw new InvalidOperationException($"Schreibzugriff außerhalb des freigegebenen Bereichs ({offset}).");
        }

        var span = _buffer.GetSpan()[..data.Length];
        data.CopyTo(span);
        System.IO.RandomAccess.Write(_handle, span, (long)offset);
    }

    public void Dispose()
    {
        _handle.Dispose();
        ((IDisposable)_buffer).Dispose();
    }
}
