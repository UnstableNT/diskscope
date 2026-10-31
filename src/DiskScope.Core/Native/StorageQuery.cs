using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.System.Ioctl;

namespace DiskScope.Core.Native;

/// <summary>Wraps IOCTL_STORAGE_QUERY_PROPERTY, including protocol-specific (ATA/NVMe) data queries.</summary>
internal static class StorageQuery
{
    // STORAGE_PROPERTY_QUERY: PropertyId (4), QueryType (4), AdditionalParameters[...]
    private const int QueryHeaderSize = 8;

    // STORAGE_PROTOCOL_DATA_DESCRIPTOR: Version (4), Size (4), STORAGE_PROTOCOL_SPECIFIC_DATA
    private const int DescriptorHeaderSize = 8;

    private static readonly int ProtocolSpecificDataSize = Unsafe.SizeOf<STORAGE_PROTOCOL_SPECIFIC_DATA>();

    /// <summary>Runs a standard property query; returns <c>null</c> if the property is not supported.</summary>
    public static byte[]? TryQuery(SafeFileHandle handle, STORAGE_PROPERTY_ID property, int outputSize = 1024)
    {
        Span<byte> query = stackalloc byte[12];
        query.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(query, (int)property);
        BinaryPrimitives.WriteInt32LittleEndian(query[4..], (int)STORAGE_QUERY_TYPE.PropertyStandardQuery);

        var output = new byte[outputSize];
        if (!DeviceIo.TryControl(handle, PInvoke.IOCTL_STORAGE_QUERY_PROPERTY, query, output, out var bytes, out var error))
        {
            if (DeviceIo.IsUnsupported(error))
            {
                return null;
            }

            throw new DiskScopeException("IOCTL_STORAGE_QUERY_PROPERTY", error, property.ToString());
        }

        return bytes == 0 ? null : output.AsSpan(0, bytes).ToArray();
    }

    /// <summary>
    /// Requests protocol-specific data (ATA IDENTIFY / log pages, NVMe Identify / log pages) through the storage stack.
    /// Returns <c>null</c> if the driver does not support the request or refuses it for lack of rights.
    /// </summary>
    public static byte[]? TryQueryProtocolData(
        SafeFileHandle handle,
        STORAGE_PROPERTY_ID property,
        STORAGE_PROTOCOL_TYPE protocol,
        uint dataType,
        uint requestValue,
        uint requestSubValue,
        int dataLength)
    {
        var size = QueryHeaderSize + ProtocolSpecificDataSize + dataLength;
        var buffer = new byte[size];

        BinaryPrimitives.WriteInt32LittleEndian(buffer, (int)property);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), (int)STORAGE_QUERY_TYPE.PropertyStandardQuery);

        var request = new STORAGE_PROTOCOL_SPECIFIC_DATA
        {
            ProtocolType = protocol,
            DataType = dataType,
            ProtocolDataRequestValue = requestValue,
            ProtocolDataRequestSubValue = requestSubValue,
            ProtocolDataOffset = (uint)ProtocolSpecificDataSize,
            ProtocolDataLength = (uint)dataLength,
        };
        Unsafe.WriteUnaligned(ref buffer[QueryHeaderSize], request);

        if (!DeviceIo.TryControl(handle, PInvoke.IOCTL_STORAGE_QUERY_PROPERTY, buffer, buffer, out var bytes, out var error))
        {
            if (DeviceIo.IsUnsupported(error) || error == DiskScopeException.ErrorAccessDenied)
            {
                return null;
            }

            throw new DiskScopeException("IOCTL_STORAGE_QUERY_PROPERTY", error, $"{protocol}/{dataType}/{requestValue}");
        }

        if (bytes < DescriptorHeaderSize + ProtocolSpecificDataSize)
        {
            return null;
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        var descriptorSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4));
        var expected = (uint)(DescriptorHeaderSize + ProtocolSpecificDataSize);
        if (version != expected || descriptorSize != expected)
        {
            return null;
        }

        var returned = Unsafe.ReadUnaligned<STORAGE_PROTOCOL_SPECIFIC_DATA>(ref buffer[DescriptorHeaderSize]);
        var start = DescriptorHeaderSize + (int)returned.ProtocolDataOffset;
        var length = (int)returned.ProtocolDataLength;
        if (returned.ProtocolDataOffset < ProtocolSpecificDataSize || length < dataLength || start + dataLength > buffer.Length)
        {
            return null;
        }

        return buffer.AsSpan(start, dataLength).ToArray();
    }
}
