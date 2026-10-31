using System.Buffers.Binary;
using System.Text;
using DiskScope.Core.Devices;

namespace DiskScope.Core.Tests;

public class StorageDescriptorsTests
{
    internal static byte[] CreateDeviceDescriptor(
        string? vendor, string product, string revision, string? serial, int busType)
    {
        var data = new byte[256];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)data.Length);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28), busType);

        var position = 64;
        void Put(int offsetField, string? text)
        {
            if (text is null)
            {
                return;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offsetField), (uint)position);
            Encoding.ASCII.GetBytes(text).CopyTo(data, position);
            position += text.Length + 1;
        }

        Put(12, vendor);
        Put(16, product);
        Put(20, revision);
        Put(24, serial);
        return data;
    }

    [Fact]
    public void Parses_device_descriptor_strings()
    {
        var data = CreateDeviceDescriptor("ACME    ", "Disk 1000 ", "R1.0", "  XYZ123 ", 11);

        var result = StorageDescriptors.ParseDevice(data)!;

        result.VendorId.ShouldBe("ACME");
        result.ProductId.ShouldBe("Disk 1000");
        result.ProductRevision.ShouldBe("R1.0");
        result.SerialNumber.ShouldBe("XYZ123");
        result.RawBusType.ShouldBe(11);
    }

    [Fact]
    public void Missing_offsets_yield_null()
    {
        var result = StorageDescriptors.ParseDevice(CreateDeviceDescriptor(null, "P", "R", null, 17))!;

        result.VendorId.ShouldBeNull();
        result.SerialNumber.ShouldBeNull();
    }

    [Fact]
    public void Too_short_descriptor_is_rejected() =>
        StorageDescriptors.ParseDevice(new byte[16]).ShouldBeNull();

    [Theory]
    [InlineData(11, BusType.Sata)]
    [InlineData(3, BusType.Sata)]
    [InlineData(17, BusType.Nvme)]
    [InlineData(7, BusType.Usb)]
    [InlineData(10, BusType.Sas)]
    [InlineData(1, BusType.Scsi)]
    [InlineData(8, BusType.Raid)]
    [InlineData(16, BusType.Raid)]
    [InlineData(14, BusType.Virtual)]
    [InlineData(15, BusType.Virtual)]
    [InlineData(0, BusType.Other)]
    public void Maps_storage_bus_types(int raw, BusType expected) =>
        StorageDescriptors.MapBusType(raw).ShouldBe(expected);

    [Fact]
    public void Parses_access_alignment()
    {
        var data = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 512);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), 4096);

        StorageDescriptors.ParseAccessAlignment(data).ShouldBe((512u, 4096u));
    }

    [Fact]
    public void Parses_boolean_descriptor()
    {
        var data = new byte[12];
        StorageDescriptors.ParseBooleanDescriptor(data).ShouldBe(false);
        data[8] = 1;
        StorageDescriptors.ParseBooleanDescriptor(data).ShouldBe(true);
        StorageDescriptors.ParseBooleanDescriptor(new byte[4]).ShouldBeNull();
    }
}
