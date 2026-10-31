using System.Buffers.Binary;
using DiskScope.Core.Devices;
using DiskScope.Core.Native;
using DiskScope.Core.Smart;
using static DiskScope.Core.Tests.SmartBuffers;

namespace DiskScope.Core.Tests;

public class SmartSupportTests
{
    private static readonly DriveInfo Drive = new(
        new DriveId(0), @"\\.\PhysicalDrive0", "SanDisk SDSSDH3 1T00", null, "S", "F", BusType.Sata, null,
        MediaKind.Ssd, 1, 512, 512, null, false, [], []);

    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Embedded_attribute_table_is_consistent()
    {
        var table = AttributeTable.Default;

        table.Lookup(194, null).Kind.ShouldBe(ValueKind.Temperature);
        table.Lookup(5, null).Name.ShouldBe("Reallocated Sectors Count");
        table.FindVendor("Samsung SSD 860 EVO")!.Name.ShouldBe("Samsung SSD");
        table.FindVendor("samsung ssd 860")!.Name.ShouldBe("Samsung SSD");
        table.FindVendor("Unknown Model").ShouldBeNull();
        table.FindVendor(null).ShouldBeNull();
    }

    [Fact]
    public void Attribute_table_entries_are_unique_per_section()
    {
        using var stream = typeof(AttributeTable).Assembly.GetManifestResourceStream("DiskScope.Core.Data.smart-attributes.json")!;
        using var reader = new System.IO.StreamReader(stream);
        var json = reader.ReadToEnd();
        var data = System.Text.Json.JsonSerializer.Deserialize(json, Smart.AttributeTableJsonContext.Default.AttributeTableData)!;

        data.Generic.Select(a => a.Id).ShouldBeUnique();
        data.Generic.ShouldAllBe(a => a.Name.Length > 0);
        foreach (var vendor in data.Vendors)
        {
            vendor.Attributes.Select(a => a.Id).ShouldBeUnique();
            vendor.ModelPrefixes.ShouldNotBeEmpty();
            vendor.Attributes.Where(a => a.Kind == ValueKind.DataSize).ShouldAllBe(a => a.UnitBytes > 0);
        }
    }

    [Theory]
    [InlineData(0xEC, 0x00, true)]
    [InlineData(0xB0, 0xD0, true)]
    [InlineData(0xB0, 0xD1, true)]
    [InlineData(0xB0, 0xDA, true)]
    [InlineData(0xB0, 0xD4, false)]   // SMART EXECUTE OFF-LINE IMMEDIATE
    [InlineData(0xB0, 0xD6, false)]   // SMART WRITE LOG
    [InlineData(0xEF, 0x42, false)]   // SET FEATURES (AAM)
    [InlineData(0xCA, 0x00, false)]   // WRITE DMA
    [InlineData(0x35, 0x00, false)]   // WRITE DMA EXT
    [InlineData(0xF4, 0x00, false)]   // SECURITY ERASE UNIT
    public void Read_only_paths_accept_only_whitelisted_commands(byte command, byte feature, bool allowed)
    {
        var taskFile = new AtaTaskFile(feature, 1, 0, 0x4F, 0xC2, 0xA0, command);

        AtaCommands.IsReadOnly(taskFile).ShouldBe(allowed);
        if (!allowed)
        {
            Should.Throw<InvalidOperationException>(() => AtaPassThrough.TryExecute(null!, taskFile, 0));
            Should.Throw<InvalidOperationException>(() => SatPassThrough.TryReadData(null!, taskFile, 512));
            Should.Throw<InvalidOperationException>(() => LegacySmart.TryReadData(null!, taskFile, 512));
        }
    }

    [Fact]
    public void Interprets_smart_return_status()
    {
        SmartProbe.InterpretReturnStatus(new AtaTaskFile(0, 0, 0, 0x4F, 0xC2, 0, 0x50)).ShouldBe(false);
        SmartProbe.InterpretReturnStatus(new AtaTaskFile(0, 0, 0, 0xF4, 0x2C, 0, 0x50)).ShouldBe(true);
        SmartProbe.InterpretReturnStatus(new AtaTaskFile(0, 0, 0, 0, 0, 0, 0x50)).ShouldBeNull();
        SmartProbe.InterpretReturnStatus(null).ShouldBeNull();
    }

    [Fact]
    public void Recognizes_smart_data_in_vendor_buffer()
    {
        SmartProbe.LooksLikeSmartData(AtaData((5, 100, 100, 0))).ShouldBeTrue();
        SmartProbe.LooksLikeSmartData(new byte[512]).ShouldBeFalse();

        var noise = new byte[512];
        noise[0] = 1;
        noise[100] = 7;
        SmartProbe.LooksLikeSmartData(noise).ShouldBeFalse();
    }

    [Fact]
    public void Parses_temperature_descriptor()
    {
        var data = new byte[40];
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(8), 84);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(10), 75);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), 1);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(26), 41);

        TemperatureDescriptor.Parse(data).ShouldBe((41, 75, 84));
        TemperatureDescriptor.Parse(new byte[40]).ShouldBeNull();
    }

    [Fact]
    public void Service_combines_ata_data_thresholds_and_status()
    {
        var raw = new RawSmartData(
            SmartSource.AtaPassThrough, AtaData((5, 100, 100, 2), (194, 70, 50, 33)), AtaThresholds((5, 10)), false, null, null, null);

        var result = SmartService.Interpret(Drive, raw, AttributeTable.Default, Now);

        result.Source.ShouldBe(SmartSource.AtaPassThrough);
        result.Overall.ShouldBe(HealthStatus.Warning);
        result.TemperatureCelsius.ShouldBe(33);
        result.Attributes.Count.ShouldBe(2);
        result.UnsupportedReason.ShouldBeNull();
    }

    [Fact]
    public void Service_reports_failing_return_status_as_error()
    {
        var raw = new RawSmartData(SmartSource.AtaPassThrough, AtaData((9, 100, 100, 1)), null, true, null, null, null);

        SmartService.Interpret(Drive, raw, AttributeTable.Default, Now).Overall.ShouldBe(HealthStatus.Error);
    }

    [Fact]
    public void Predict_failure_without_data_gives_yes_no_status()
    {
        var raw = new RawSmartData(SmartSource.PredictFailure, null, null, false, null, null, "Adminrechte fehlen");

        var result = SmartService.Interpret(Drive, raw, AttributeTable.Default, Now);

        result.Overall.ShouldBe(HealthStatus.Ok);
        result.Attributes.ShouldBeEmpty();
        result.UnsupportedReason.ShouldBe("Adminrechte fehlen");
        result.IsSupported.ShouldBeTrue();
    }

    [Fact]
    public void Nothing_available_is_unsupported_not_an_error()
    {
        var temperature = new byte[40];
        BinaryPrimitives.WriteUInt16LittleEndian(temperature.AsSpan(12), 1);
        BinaryPrimitives.WriteInt16LittleEndian(temperature.AsSpan(26), 39);
        var raw = new RawSmartData(SmartSource.None, null, null, null, null, temperature, "nicht verfügbar");

        var result = SmartService.Interpret(Drive, raw, AttributeTable.Default, Now);

        result.IsSupported.ShouldBeFalse();
        result.Overall.ShouldBe(HealthStatus.Unknown);
        result.TemperatureCelsius.ShouldBe(39);
    }

    [Fact]
    public void Nvme_result_uses_composite_temperature()
    {
        var raw = new RawSmartData(SmartSource.NvmeLog, null, null, null, NvmeLog(kelvin: 320), null, null);

        var result = SmartService.Interpret(Drive, raw, AttributeTable.Default, Now);

        result.TemperatureCelsius.ShouldBe(47);
        result.Overall.ShouldBe(HealthStatus.Ok);
    }

    [Theory]
    [InlineData(null, "–")]
    [InlineData("AB", "**")]
    [InlineData("X9TEST0SERIAL7Q", "***********AL7Q")]
    public void Masks_serial_numbers(string? serial, string expected) => LogMask.Serial(serial).ShouldBe(expected);

    [Fact]
    public void Default_temperature_warning_depends_on_media()
    {
        TemperatureLimits.DefaultWarningCelsius(MediaKind.Hdd).ShouldBe(50);
        TemperatureLimits.DefaultWarningCelsius(MediaKind.Ssd).ShouldBe(70);
        TemperatureLimits.ToFahrenheit(100).ShouldBe(212);
    }
}
