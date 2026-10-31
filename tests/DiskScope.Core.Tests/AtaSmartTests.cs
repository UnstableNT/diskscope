using DiskScope.Core.Smart;
using static DiskScope.Core.Tests.SmartBuffers;

namespace DiskScope.Core.Tests;

public class AtaSmartTests
{
    private static readonly AttributeTable Table = AttributeTable.Default;

    [Fact]
    public void Parses_entries_and_matches_thresholds()
    {
        var data = AtaData((5, 100, 100, 0), (9, 98, 98, 0x1C16), (194, 72, 52, 0x0034_000B_001C));
        var thresholds = AtaThresholds((5, 10), (9, 0), (194, 0));

        var rows = AtaSmart.Parse(data, thresholds, Table, "Some Disk");

        rows.Count.ShouldBe(3);
        rows[0].ShouldBe(new SmartEntry(5, "Reallocated Sectors Count", ValueKind.Count, "0", 100, 100, 10, "000000000000", 0, HealthStatus.Ok));
        rows[1].Value.ShouldBe("7190 h");
        rows[1].Raw.ShouldBe("000000001C16");
        rows[2].Value.ShouldBe("28 °C");
        rows[2].Worst.ShouldBe((byte)52);
    }

    [Fact]
    public void Missing_thresholds_leave_threshold_empty()
    {
        var rows = AtaSmart.Parse(AtaData((12, 100, 100, 1979)), [], Table, null);

        rows.Single().Threshold.ShouldBeNull();
        rows.Single().Value.ShouldBe("1979");
    }

    [Fact]
    public void Skips_empty_slots_and_names_unknown_ids()
    {
        var rows = AtaSmart.Parse(AtaData((0, 0, 0, 0), (250, 100, 100, 7)), [], Table, null);

        rows.Single().Id.ShouldBe(250);
        rows.Single().Name.ShouldBe("Unbekanntes Attribut");
        rows.Single().Kind.ShouldBe(ValueKind.Raw);
    }

    [Fact]
    public void Vendor_section_overrides_generic_definition()
    {
        var data = AtaData((241, 253, 253, 0x7D64));

        AtaSmart.Parse(data, [], Table, "SanDisk SDSSDH3 1T00").Single().Value.ShouldBe("34.5 TB");
        AtaSmart.Parse(data, [], Table, "Generic Disk").Single().Value.ShouldBe("16.4 MB");
    }

    [Fact]
    public void Hours_use_only_the_low_four_raw_bytes()
    {
        var rows = AtaSmart.Parse(AtaData((9, 90, 90, 0x1234_0000_0064)), [], Table, null);

        rows.Single().Value.ShouldBe("100 h");
    }

    [Fact]
    public void Percent_can_come_from_the_normalized_value()
    {
        var rows = AtaSmart.Parse(AtaData((232, 97, 97, 0x64)), [], Table, "SanDisk SDSSDH3");

        rows.Single().Value.ShouldBe("97 %");
    }

    [Theory]
    [InlineData(194, 0x1CUL, 28)]
    [InlineData(190, 0x2AUL, 42)]
    public void Temperature_from_194_or_190(byte id, ulong raw, int expected) =>
        AtaSmart.Temperature(AtaSmart.Parse(AtaData((id, 70, 50, raw)), [], Table, null)).ShouldBe(expected);

    [Fact]
    public void Temperature_prefers_194()
    {
        var rows = AtaSmart.Parse(AtaData((190, 60, 50, 40), (194, 70, 50, 30)), [], Table, null);

        AtaSmart.Temperature(rows).ShouldBe(30);
    }

    [Fact]
    public void No_temperature_without_attribute() =>
        AtaSmart.Temperature(AtaSmart.Parse(AtaData((5, 100, 100, 0)), [], Table, null)).ShouldBeNull();

    [Fact]
    public void Rejects_short_buffer() =>
        Should.Throw<ArgumentException>(() => AtaSmart.Parse(new byte[100], [], Table, null));
}
