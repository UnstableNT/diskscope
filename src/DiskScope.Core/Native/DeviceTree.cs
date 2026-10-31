using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Devices.DeviceAndDriverInstallation;
using Windows.Win32.Foundation;
using Windows.Win32.System.Ioctl;

namespace DiskScope.Core.Native;

/// <summary>A disk device interface with its PhysicalDrive number and PnP device node.</summary>
internal readonly record struct DiskInterface(int Number, string InterfacePath, uint DevInst);

/// <summary>PCI Express link state of an upstream PCI device (DEVPKEY_PciDevice_*).</summary>
internal readonly record struct PciLink(uint CurrentSpeed, uint CurrentWidth, uint MaxSpeed, uint MaxWidth);

/// <summary>Enumerates disks via the configuration manager (GUID_DEVINTERFACE_DISK).</summary>
internal static class DeviceTree
{
    private const int MaxParentDepth = 6;

    public static unsafe IReadOnlyList<DiskInterface> EnumerateDisks()
    {
        var guid = PInvoke.GUID_DEVINTERFACE_DISK;
        char[] list;
        while (true)
        {
            var cr = PInvoke.CM_Get_Device_Interface_List_Size(
                out var length, guid, default, CM_GET_DEVICE_INTERFACE_LIST_FLAGS.CM_GET_DEVICE_INTERFACE_LIST_PRESENT);
            ThrowOnError(cr, nameof(PInvoke.CM_Get_Device_Interface_List_Size));

            list = new char[length];
            fixed (char* p = list)
            {
                cr = PInvoke.CM_Get_Device_Interface_List(
                    guid, default, new PZZWSTR(p), length, CM_GET_DEVICE_INTERFACE_LIST_FLAGS.CM_GET_DEVICE_INTERFACE_LIST_PRESENT);
            }

            if (cr != CONFIGRET.CR_BUFFER_SMALL)
            {
                ThrowOnError(cr, nameof(PInvoke.CM_Get_Device_Interface_List));
                break;
            }
        }

        var result = new List<DiskInterface>();
        foreach (var path in SplitMultiString(list))
        {
            var number = TryGetDeviceNumber(path);
            if (number is null)
            {
                continue;
            }

            result.Add(new DiskInterface(number.Value, path, TryLocateDevNode(path)));
        }

        result.Sort((a, b) => a.Number.CompareTo(b.Number));
        return result;
    }

    /// <summary>Walks up the device tree from a disk and returns the link state of the first PCI device found.</summary>
    public static PciLink? TryGetPciLink(uint devInst)
    {
        var node = devInst;
        for (var depth = 0; depth < MaxParentDepth && node != 0; depth++)
        {
            var speed = TryGetUInt32(node, PInvoke.DEVPKEY_PciDevice_CurrentLinkSpeed);
            var width = TryGetUInt32(node, PInvoke.DEVPKEY_PciDevice_CurrentLinkWidth);
            if (speed is not null && width is not null)
            {
                return new PciLink(
                    speed.Value,
                    width.Value,
                    TryGetUInt32(node, PInvoke.DEVPKEY_PciDevice_MaxLinkSpeed) ?? 0,
                    TryGetUInt32(node, PInvoke.DEVPKEY_PciDevice_MaxLinkWidth) ?? 0);
            }

            if (PInvoke.CM_Get_Parent(out var parent, node, 0) != CONFIGRET.CR_SUCCESS)
            {
                break;
            }

            node = parent;
        }

        return null;
    }

    internal static IEnumerable<string> SplitMultiString(ReadOnlyMemory<char> buffer)
    {
        var start = 0;
        for (var i = 0; i < buffer.Length; i++)
        {
            if (buffer.Span[i] != '\0')
            {
                continue;
            }

            if (i == start)
            {
                yield break;
            }

            yield return new string(buffer.Span[start..i]);
            start = i + 1;
        }
    }

    private static int? TryGetDeviceNumber(string interfacePath)
    {
        try
        {
            using var handle = DeviceIo.Open(interfacePath, DeviceAccess.Query);
            Span<byte> output = stackalloc byte[Marshal.SizeOf<STORAGE_DEVICE_NUMBER>()];
            if (!DeviceIo.TryControl(handle, PInvoke.IOCTL_STORAGE_GET_DEVICE_NUMBER, default, output, out _, out _))
            {
                return null;
            }

            return (int)MemoryMarshal.Read<STORAGE_DEVICE_NUMBER>(output).DeviceNumber;
        }
        catch (DiskScopeException)
        {
            return null;
        }
    }

    private static unsafe uint TryLocateDevNode(string interfacePath)
    {
        uint size = 512;
        var buffer = new byte[size];
        var cr = PInvoke.CM_Get_Device_Interface_Property(
            interfacePath, PInvoke.DEVPKEY_Device_InstanceId, out _, buffer, ref size, 0);
        if (cr != CONFIGRET.CR_SUCCESS)
        {
            return 0;
        }

        var instanceId = MemoryMarshal.Cast<byte, char>(buffer.AsSpan(0, (int)size)).ToString().TrimEnd('\0');
        fixed (char* p = instanceId)
        {
            return PInvoke.CM_Locate_DevNode(out var devInst, new PWSTR(p), CM_LOCATE_DEVNODE_FLAGS.CM_LOCATE_DEVNODE_NORMAL)
                == CONFIGRET.CR_SUCCESS ? devInst : 0;
        }
    }

    private static uint? TryGetUInt32(uint devInst, in DEVPROPKEY key)
    {
        Span<byte> buffer = stackalloc byte[4];
        uint size = 4;
        var cr = PInvoke.CM_Get_DevNode_Property(devInst, key, out _, buffer, ref size, 0);
        return cr == CONFIGRET.CR_SUCCESS && size == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(buffer) : null;
    }

    private static void ThrowOnError(CONFIGRET cr, string function)
    {
        if (cr != CONFIGRET.CR_SUCCESS)
        {
            throw new DiskScopeException(function, (int)PInvoke.CM_MapCrToWin32Err(cr, 0));
        }
    }
}
