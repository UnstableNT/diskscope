using DiskScope.Core.Devices;
using static DiskScope.Core.Tests.TestBuffers;

namespace DiskScope.Core.Tests;

public class AtaIdentifyTests
{
    private static byte[] CreateIdentify()
    {
        var data = new byte[AtaIdentify.Length];
        SetAtaString(data, 10, 19, "SN1234567890");
        SetAtaString(data, 23, 26, "FW01.2");
        SetAtaString(data, 27, 46, "Example SSD 512GB");
        return data;
    }

    [Fact]
    public void Parses_byte_swapped_strings()
    {
        var result = AtaIdentify.Parse(CreateIdentify());

        result.Model.ShouldBe("Example SSD 512GB");
        result.Serial.ShouldBe("SN1234567890");
        result.Firmware.ShouldBe("FW01.2");
    }

    [Fact]
    public void Rejects_short_buffer() =>
        Should.Throw<ArgumentException>(() => AtaIdentify.Parse(new byte[100]));

    [Theory]
    [InlineData(0x000E, 0x0006, 3, 3, "SATA 3 (6.0 Gb/s)")]
    [InlineData(0x000E, 0x0004, 2, 3, "SATA 2 (3.0 Gb/s), max. SATA 3")]
    [InlineData(0x0006, 0x0002, 1, 2, "SATA 1 (1.5 Gb/s), max. SATA 2")]
    public void Reads_sata_link_speed(ushort word76, ushort word77, int current, int max, string link)
    {
        var data = CreateIdentify();
        SetWord(data, 76, word76);
        SetWord(data, 77, word77);

        var result = AtaIdentify.Parse(data);

        result.CurrentSataGeneration.ShouldBe(current);
        result.MaxSataGeneration.ShouldBe(max);
        result.Link.ShouldBe(link);
    }

    [Fact]
    public void No_link_when_sata_words_are_not_reported()
    {
        var result = AtaIdentify.Parse(CreateIdentify());

        result.Link.ShouldBeNull();
    }

    [Fact]
    public void Reads_supported_and_enabled_feature_bits()
    {
        var data = CreateIdentify();
        // Word 82: SMART (0), security (1), write cache (5), look-ahead (6), HPA (10)
        SetWord(data, 82, 0b0000_0100_0110_0011);
        // Word 83: validity 01b, microcode (0), APM (3), AAM (9), 48-bit (10)
        SetWord(data, 83, 0x4000 | 0b0000_0110_0000_1001);
        // Word 85: SMART and write cache enabled, look-ahead disabled
        SetWord(data, 85, 0b0000_0000_0010_0001);
        // Word 86: APM enabled, 48-bit enabled
        SetWord(data, 86, 0b0000_0100_0000_1000);
        // Word 87: validity 01b
        SetWord(data, 87, 0x4000);
        // Word 76: Gen3 + NCQ (8) + HIPM (9)
        SetWord(data, 76, 0x0308);
        // Word 169: TRIM, word 206: SCT
        SetWord(data, 169, 1);
        SetWord(data, 206, 1);

        var features = AtaIdentify.Parse(data).Features.ToDictionary(f => f.Kind);

        features[FeatureKind.Smart].ShouldBe(new Feature(FeatureKind.Smart, true, true));
        features[FeatureKind.Security].ShouldBe(new Feature(FeatureKind.Security, true, false));
        features[FeatureKind.WriteCache].ShouldBe(new Feature(FeatureKind.WriteCache, true, true));
        features[FeatureKind.ReadLookAhead].ShouldBe(new Feature(FeatureKind.ReadLookAhead, true, false));
        features[FeatureKind.HostProtectedArea].Supported.ShouldBeTrue();
        features[FeatureKind.FirmwareUpdate].Supported.ShouldBeTrue();
        features[FeatureKind.AdvancedPowerManagement].ShouldBe(new Feature(FeatureKind.AdvancedPowerManagement, true, true));
        features[FeatureKind.AutomaticAcousticManagement].ShouldBe(new Feature(FeatureKind.AutomaticAcousticManagement, true, false));
        features[FeatureKind.Lba48].ShouldBe(new Feature(FeatureKind.Lba48, true, true));
        features[FeatureKind.Ncq].Supported.ShouldBeTrue();
        features[FeatureKind.InterfacePowerManagement].Supported.ShouldBeTrue();
        features[FeatureKind.Trim].Supported.ShouldBeTrue();
        features[FeatureKind.Sct].Supported.ShouldBeTrue();
        features[FeatureKind.PowerUpInStandby].Supported.ShouldBeFalse();
        features[FeatureKind.DeviceConfigurationOverlay].Supported.ShouldBeFalse();
        features.Count.ShouldBe(Enum.GetValues<FeatureKind>().Length);
    }

    [Fact]
    public void Ignores_command_set_words_without_validity_signature()
    {
        var data = CreateIdentify();
        SetWord(data, 82, 0xFFFF);
        SetWord(data, 83, 0x0FFF); // bits 15:14 = 00b -> invalid

        var features = AtaIdentify.Parse(data).Features;

        features.Single(f => f.Kind == FeatureKind.Smart).Supported.ShouldBeFalse();
        features.Single(f => f.Kind == FeatureKind.Lba48).Supported.ShouldBeFalse();
        features.Single(f => f.Kind == FeatureKind.Smart).Enabled.ShouldBeNull();
    }

    [Theory]
    [InlineData((ushort)0, null)]
    [InlineData((ushort)0xFFFF, null)]
    [InlineData((ushort)16384, 8388608UL)]
    public void Reads_buffer_size_from_word_21(ushort word21, ulong? expected)
    {
        var data = CreateIdentify();
        SetWord(data, 21, word21);

        AtaIdentify.Parse(data).BufferBytes.ShouldBe(expected);
    }

    [Fact]
    public void Reads_rotation_rate()
    {
        var data = CreateIdentify();
        SetWord(data, 217, 7200);

        AtaIdentify.Parse(data).RotationRate.ShouldBe((ushort)7200);
    }
}
