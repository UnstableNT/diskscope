using DiskScope.Core.Devices;
using static DiskScope.Core.Tests.TestBuffers;

namespace DiskScope.Core.Tests;

public class NvmeIdentifyTests
{
    private static byte[] CreateIdentify()
    {
        var data = new byte[NvmeIdentify.Length];
        data[0] = 0x4D;
        data[1] = 0x14;
        SetAscii(data, 4, 20, "S0MESERIAL42");
        SetAscii(data, 24, 40, "Example NVMe 2TB");
        SetAscii(data, 64, 8, "1B2QEXM7");
        return data;
    }

    [Fact]
    public void Parses_identity_strings()
    {
        var result = NvmeIdentify.Parse(CreateIdentify());

        result.PciVendorId.ShouldBe((ushort)0x144D);
        result.Serial.ShouldBe("S0MESERIAL42");
        result.Model.ShouldBe("Example NVMe 2TB");
        result.Firmware.ShouldBe("1B2QEXM7");
    }

    [Fact]
    public void Maps_controller_capabilities_to_features()
    {
        var data = CreateIdentify();
        data[256] = 0b0000_0101; // OACS: security + firmware download
        data[263] = 4;           // NPSS: five power states
        data[265] = 1;           // APSTA
        data[520] = 0b0000_0100; // ONCS: dataset management
        data[525] = 1;           // VWC present

        var features = NvmeIdentify.Parse(data).Features.ToDictionary(f => f.Kind);

        features[FeatureKind.Smart].ShouldBe(new Feature(FeatureKind.Smart, true, true));
        features[FeatureKind.Security].Supported.ShouldBeTrue();
        features[FeatureKind.FirmwareUpdate].Supported.ShouldBeTrue();
        features[FeatureKind.PowerManagement].Supported.ShouldBeTrue();
        features[FeatureKind.AdvancedPowerManagement].Supported.ShouldBeTrue();
        features[FeatureKind.Trim].Supported.ShouldBeTrue();
        features[FeatureKind.WriteCache].Supported.ShouldBeTrue();
        features.ShouldNotContainKey(FeatureKind.AutomaticAcousticManagement);
        features.ShouldNotContainKey(FeatureKind.Ncq);
    }

    [Fact]
    public void Reports_missing_capabilities_as_unsupported()
    {
        var features = NvmeIdentify.Parse(CreateIdentify()).Features;

        features.Where(f => f.Kind != FeatureKind.Smart).ShouldAllBe(f => !f.Supported);
    }
}
