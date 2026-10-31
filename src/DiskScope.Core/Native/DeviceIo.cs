using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace DiskScope.Core.Native;

/// <summary>How a device handle is opened. Raw writes are not available here (SPEC 9).</summary>
internal enum DeviceAccess
{
    /// <summary>No data access; enough for IOCTL_STORAGE_QUERY_PROPERTY and other FILE_ANY_ACCESS queries, works without admin.</summary>
    Query,

    /// <summary>GENERIC_READ for reading sectors.</summary>
    Read,

    /// <summary>
    /// GENERIC_READ | GENERIC_WRITE, required by IOCTL_ATA_PASS_THROUGH even for non-data or data-in commands.
    /// Only <see cref="AtaPassThrough"/> uses such handles, and it refuses data-out commands.
    /// </summary>
    Command,

    /// <summary>
    /// GENERIC_READ | GENERIC_WRITE for writing sectors or locking volumes. Reserved for DiskScope.Core.Destructive,
    /// which only opens such handles after <c>WriteGuard</c> granted a <c>WriteAuthorization</c> (SPEC 9.1);
    /// an architecture test checks that no other namespace uses it.
    /// </summary>
    Write,
}

internal static class DeviceIo
{
    private const int ErrorInvalidFunction = 1;
    private const int ErrorNotSupported = 50;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorMoreData = 234;

    public static SafeFileHandle Open(string path, DeviceAccess access, FILE_FLAGS_AND_ATTRIBUTES flags = 0)
    {
        uint desired = access switch
        {
            DeviceAccess.Query => 0,
            DeviceAccess.Read => (uint)GENERIC_ACCESS_RIGHTS.GENERIC_READ,
            DeviceAccess.Command or DeviceAccess.Write => (uint)(GENERIC_ACCESS_RIGHTS.GENERIC_READ | GENERIC_ACCESS_RIGHTS.GENERIC_WRITE),
            _ => throw new ArgumentOutOfRangeException(nameof(access)),
        };

        var handle = PInvoke.CreateFile(
            path,
            desired,
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE,
            null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            flags,
            null);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new DiskScopeException(nameof(PInvoke.CreateFile), error, path);
        }

        return handle;
    }

    /// <summary>Sends an IOCTL and returns the number of bytes written to <paramref name="output"/>.</summary>
    public static int Control(SafeFileHandle handle, uint code, ReadOnlySpan<byte> input, Span<byte> output, string name)
    {
        if (!TryControl(handle, code, input, output, out var bytes, out var error))
        {
            throw new DiskScopeException(name, error);
        }

        return bytes;
    }

    /// <summary>Sends an IOCTL; returns <c>false</c> with the Win32 error code on failure.</summary>
    public static unsafe bool TryControl(
        SafeFileHandle handle, uint code, ReadOnlySpan<byte> input, Span<byte> output, out int bytesReturned, out int error)
    {
        if (PInvoke.DeviceIoControl(handle, code, input, output, out var returned, null))
        {
            bytesReturned = (int)returned;
            error = 0;
            return true;
        }

        bytesReturned = 0;
        error = Marshal.GetLastPInvokeError();

        // ERROR_MORE_DATA still delivers a usable (partial) buffer.
        if (error == ErrorMoreData)
        {
            bytesReturned = output.Length;
            return true;
        }

        return false;
    }

    /// <summary>Errors that mean "this request is not supported by the driver/device", not a failure.</summary>
    public static bool IsUnsupported(int error) =>
        error is ErrorInvalidFunction or ErrorNotSupported or ErrorInvalidParameter or ErrorInsufficientBuffer;
}
