using DiskScope.Core.Devices;
using DiskScope.Core.Native;
using DiskScope.Core.Smart;
using static DiskScope.Core.Tests.SmartBuffers;

namespace DiskScope.Core.Tests;

public sealed class SelfTestAndHistoryTests : IDisposable
{
    private static readonly DriveInfo Drive = new(
        new DriveId(0), @"\\.\PhysicalDrive0", "Test Disk", null, "SER123", "F", BusType.Sata, null,
        MediaKind.Ssd, 1, 512, 512, null, false, [], []);

    private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DiskScopeTests", "history-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (System.IO.Directory.Exists(_directory))
        {
            System.IO.Directory.Delete(_directory, recursive: true);
        }
    }

    private static byte[] SelfTestData(byte status, byte capability = 0x01, byte shortMinutes = 2, byte extendedMinutes = 85)
    {
        var data = AtaData((5, 100, 100, 0));
        data[363] = status;
        data[367] = capability;
        data[372] = shortMinutes;
        data[373] = extendedMinutes;
        return data;
    }

    [Theory]
    [InlineData(0x00, SelfTestState.CompletedOk, null)]
    [InlineData(0x10, SelfTestState.Aborted, null)]
    [InlineData(0x20, SelfTestState.Interrupted, null)]
    [InlineData(0xF7, SelfTestState.Running, 70)]
    [InlineData(0x71, SelfTestState.Failed, null)]
    public void Parses_self_test_execution_status(byte status, SelfTestState state, int? remaining)
    {
        var result = SelfTestService.Parse(SelfTestData(status));

        result.State.ShouldBe(state);
        result.RemainingPercent.ShouldBe(remaining);
        result.ShortMinutes.ShouldBe(2);
        result.ExtendedMinutes.ShouldBe(85);
    }

    [Fact]
    public void Extended_polling_time_from_word_375_when_byte_373_is_ff()
    {
        var data = SelfTestData(0, extendedMinutes: 0xFF);
        data[375] = 0x2C;
        data[376] = 0x01;

        SelfTestService.Parse(data).ExtendedMinutes.ShouldBe(300);
    }

    [Fact]
    public void Missing_capability_means_not_supported() =>
        SelfTestService.Parse(SelfTestData(0, capability: 0)).State.ShouldBe(SelfTestState.NotSupported);

    [Theory]
    [InlineData(AtaCommands.SelfTestShort, true)]
    [InlineData(AtaCommands.SelfTestExtended, true)]
    [InlineData(AtaCommands.SelfTestAbort, true)]
    [InlineData(0x81, false)] // captive short test would block the drive
    [InlineData(0x04, false)]
    public void Only_background_self_tests_are_allowed(byte subcommand, bool allowed)
    {
        AtaCommands.IsSelfTestControl(AtaCommands.SmartSelfTest(subcommand)).ShouldBe(allowed);
        if (!allowed)
        {
            Should.Throw<InvalidOperationException>(() => AtaPassThrough.TryExecute(null!, AtaCommands.SmartSelfTest(subcommand), 0));
        }
    }

    [Fact]
    public void Self_test_commands_cannot_transfer_data() =>
        Should.Throw<InvalidOperationException>(() => AtaPassThrough.TryExecute(null!, AtaCommands.SmartSelfTest(AtaCommands.SelfTestShort), 512));

    [Fact]
    public void Nvme_self_test_is_reported_as_unsupported() =>
        new SelfTestService().Query(Drive with { Bus = BusType.Nvme }).State.ShouldBe(SelfTestState.NotSupported);

    [Fact]
    public void History_stores_only_changed_queries_and_lists_changes()
    {
        var history = new SmartHistory(_directory);
        var t0 = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

        history.Append(Drive, Result(t0, (5, 100, 0), (9, 100, 100))).ShouldBeTrue();
        history.Append(Drive, Result(t0.AddHours(1), (5, 100, 0), (9, 100, 100))).ShouldBeFalse();
        history.Append(Drive, Result(t0.AddHours(2), (5, 99, 3), (9, 100, 102))).ShouldBeTrue();

        var entries = history.Load(Drive);
        entries.Count.ShouldBe(2);

        var changes = SmartHistory.Changes(entries);
        changes.Select(c => c.Id).ShouldBe([5, 9]);
        changes[0].Before.ShouldBe("0");
        changes[0].After.ShouldBe("3");
        changes[0].Timestamp.ShouldBe(t0.AddHours(2));
    }

    [Fact]
    public void History_files_are_separate_per_drive_and_skip_damaged_lines()
    {
        var history = new SmartHistory(_directory);
        history.Append(Drive, Result(DateTimeOffset.Now, (5, 100, 0)));
        System.IO.File.AppendAllText(history.PathFor(Drive), "{kaputt\n");

        history.Load(Drive).Count.ShouldBe(1);
        history.Load(Drive with { Serial = "OTHER" }).ShouldBeEmpty();
    }

    private static SmartResult Result(DateTimeOffset time, params (int Id, byte Current, ulong Raw)[] values) =>
        new(
            Drive.Id,
            SmartSource.AtaPassThrough,
            HealthStatus.Ok,
            null,
            values.Select(v => new SmartEntry(v.Id, $"Attr {v.Id}", ValueKind.Count, v.Raw.ToString(System.Globalization.CultureInfo.InvariantCulture), v.Current, v.Current, 0, "", v.Raw, HealthStatus.Ok)).ToArray(),
            null,
            time);
}
