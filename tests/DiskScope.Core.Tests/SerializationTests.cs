using System.Text.Json;
using DiskScope.Core.Devices;
using DiskScope.Core.Serialization;

namespace DiskScope.Core.Tests;

public class SerializationTests
{
    private static readonly DriveInfo Sample = new(
        new DriveId(4),
        @"\\.\PhysicalDrive4",
        "Model",
        null,
        "SER",
        "FW",
        BusType.Nvme,
        "PCIe 4.0 x4",
        MediaKind.Ssd,
        2_000_398_934_016,
        512,
        4096,
        null,
        false,
        [new VolumeInfo("Daten", "D:", 1000, 400, "NTFS", "ABCD-EF01", 1_048_576)],
        [new Feature(FeatureKind.Trim, true, null)]);

    [Fact]
    public void Writes_drive_id_as_number_and_enums_as_strings()
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(Sample, DiskScopeJsonContext.Default.DriveInfo));
        var root = json.RootElement;

        root.GetProperty("id").GetInt32().ShouldBe(4);
        root.GetProperty("bus").GetString().ShouldBe("Nvme");
        root.GetProperty("media").GetString().ShouldBe("Ssd");
        root.GetProperty("sizeBytes").GetUInt64().ShouldBe(2_000_398_934_016UL);
        root.GetProperty("sizeDisplay").GetString().ShouldBe("2 TB");
        root.GetProperty("features")[0].GetProperty("kind").GetString().ShouldBe("Trim");
        root.GetProperty("volumes")[0].GetProperty("usedPercent").GetDouble().ShouldBe(60.0);
    }

    [Fact]
    public void Round_trips_drive_info()
    {
        var text = JsonSerializer.Serialize(Sample, DiskScopeJsonContext.Default.DriveInfo);

        var back = JsonSerializer.Deserialize(text, DiskScopeJsonContext.Default.DriveInfo)!;

        back.Id.ShouldBe(Sample.Id);
        back.Volumes.ShouldBe(Sample.Volumes);
        back.Features.ShouldBe(Sample.Features);
    }

    [Theory]
    [InlineData(0UL, "0 B")]
    [InlineData(999UL, "999 B")]
    [InlineData(1_000_204_886_016UL, "1 TB")]
    [InlineData(555_700_000_000UL, "555.7 GB")]
    public void Formats_decimal_sizes(ulong bytes, string expected) => ByteSize.Format(bytes).ShouldBe(expected);

    [Theory]
    [InlineData(65536UL, "64 KiB")]
    [InlineData(8_388_608UL, "8 MiB")]
    public void Formats_binary_sizes(ulong bytes, string expected) => ByteSize.FormatBinary(bytes).ShouldBe(expected);
}
