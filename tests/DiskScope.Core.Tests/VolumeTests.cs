using System.Buffers.Binary;
using DiskScope.Core.Devices;
using DiskScope.Core.Native;

namespace DiskScope.Core.Tests;

public class VolumeTests
{
    [Fact]
    public void Parses_volume_disk_extents()
    {
        var data = new byte[8 + (2 * 24)];
        BinaryPrimitives.WriteInt32LittleEndian(data, 2);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(16), 1_048_576);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(24), 500_000_000);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(32), 3);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(40), 2_097_152);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(48), 100);

        Volumes.ParseExtents(data).ShouldBe(
        [
            new VolumeExtent(1, 1_048_576, 500_000_000),
            new VolumeExtent(3, 2_097_152, 100),
        ]);
    }

    [Fact]
    public void Truncated_extent_buffer_is_tolerated()
    {
        var data = new byte[8 + 10];
        BinaryPrimitives.WriteInt32LittleEndian(data, 1);

        Volumes.ParseExtents(data).ShouldBeEmpty();
    }

    [Fact]
    public void Splits_multi_strings()
    {
        var buffer = "C:\\\0D:\\mount\\\0\0garbage".ToCharArray();

        DeviceTree.SplitMultiString(buffer).ShouldBe(["C:\\", "D:\\mount\\"]);
    }

    [Fact]
    public void Formats_volume_serial() => DriveService.FormatSerial(0x3A888370).ShouldBe("3A88-8370");

    [Fact]
    public void Assigns_volume_extents_to_their_disk_sorted_by_offset()
    {
        RawVolume[] volumes =
        [
            new(@"\\?\Volume{b}\", null, [@"D:\"], [new VolumeExtent(0, 2_000_000, 50)], "Data", "NTFS", 0x12345678, 1000, 250),
            new(@"\\?\Volume{a}\", null, [], [new VolumeExtent(0, 1_048_576, 50)], "", "FAT32", null, null, null),
            new(@"\\?\Volume{c}\", null, [@"E:\"], [new VolumeExtent(1, 1_048_576, 50)], "Other", "NTFS", 1, 1, 1),
        ];

        var result = DriveService.VolumesOnDisk(0, volumes);

        result.Count.ShouldBe(2);
        result[0].ShouldBe(new VolumeInfo("", "", 50, 0, "FAT32", "", 1_048_576));
        result[1].ShouldBe(new VolumeInfo("Data", "D:", 1000, 250, "NTFS", "1234-5678", 2_000_000));
        result[1].UsedPercent.ShouldBe(75.0);
    }
}
