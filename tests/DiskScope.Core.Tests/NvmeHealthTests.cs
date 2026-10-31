using System.Buffers.Binary;
using DiskScope.Core.Smart;
using static DiskScope.Core.Tests.SmartBuffers;

namespace DiskScope.Core.Tests;

public class NvmeHealthTests
{
    [Fact]
    public void Converts_kelvin_to_celsius()
    {
        var log = NvmeLog(kelvin: 325);

        NvmeHealth.Temperature(log).ShouldBe(52);
        NvmeHealth.Parse(log).Single(r => r.Id == NvmeHealth.CompositeTemperature).Value.ShouldBe("52 °C");
    }

    [Fact]
    public void Zero_kelvin_means_no_temperature() => NvmeHealth.Temperature(NvmeLog(kelvin: 0)).ShouldBeNull();

    [Fact]
    public void Data_units_are_512000_bytes()
    {
        var rows = NvmeHealth.Parse(NvmeLog(unitsRead: 142_578_184, unitsWritten: 2));

        var read = rows.Single(r => r.Id == NvmeHealth.DataUnitsRead);
        read.RawValue.ShouldBe(142_578_184UL);
        read.Value.ShouldBe("73 TB");
        rows.Single(r => r.Id == NvmeHealth.DataUnitsWritten).Value.ShouldBe("1 MB");
    }

    [Fact]
    public void Reads_counters_and_percentages()
    {
        var rows = NvmeHealth.Parse(NvmeLog(spare: 99, spareThreshold: 10, used: 7, powerOnHours: 2671))
            .ToDictionary(r => r.Id);

        rows[NvmeHealth.AvailableSpare].Value.ShouldBe("99 %");
        rows[NvmeHealth.AvailableSpareThreshold].Value.ShouldBe("10 %");
        rows[NvmeHealth.PercentageUsed].Value.ShouldBe("7 %");
        rows[NvmeHealth.PowerOnHours].Value.ShouldBe("2671 h");
        rows[NvmeHealth.PowerCycles].RawValue.ShouldBe(42UL);
        rows.Count.ShouldBe(17);
    }

    [Fact]
    public void Huge_128_bit_counters_saturate()
    {
        var log = NvmeLog();
        BinaryPrimitives.WriteUInt64LittleEndian(log.AsSpan(NvmeHealth.HostReadCommands + 8), 1);

        NvmeHealth.ReadCounter(log, NvmeHealth.HostReadCommands).ShouldBe(ulong.MaxValue);
    }

    [Fact]
    public void Rejects_short_log() => Should.Throw<ArgumentException>(() => NvmeHealth.Parse(new byte[64]));
}
