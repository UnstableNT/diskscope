using DiskScope.Core.Benchmarks;
using DiskScope.Core.Destructive;
using DiskScope.Core.Devices;

namespace DiskScope.Core.Tests;

/// <summary>Protection rules of SPEC 9 and the erase engine.</summary>
public class DestructiveTests
{
    private const ulong MiB = 1024 * 1024;

    private static readonly DriveInfo Empty = new(
        new DriveId(7), @"\\.\PhysicalDrive7", "Virtual Disk", "Msft", "SER-42", "1.0", BusType.Virtual, null,
        MediaKind.Unknown, 64 * MiB, 512, 512, null, false, [], []);

    private static readonly VolumeInfo Mounted = new("Daten", "X:", 1000, 500, "NTFS", "1234-5678", 1048576);

    [Fact]
    public void Empty_non_system_drive_is_allowed()
    {
        var plan = WriteGuard.Evaluate(Empty, null, dismountVolumes: false);

        plan.Allowed.ShouldBeTrue();
        plan.Range.ShouldBe(new DriveRange(0, 64 * MiB));
    }

    [Fact]
    public void System_disk_is_always_refused_even_with_dismount()
    {
        var plan = WriteGuard.Evaluate(Empty with { IsSystemDisk = true }, null, dismountVolumes: true);

        plan.Allowed.ShouldBeFalse();
        plan.Refusals.ShouldContain(r => r.Contains("System", StringComparison.Ordinal));
    }

    [Fact]
    public void Mounted_volumes_are_refused_unless_dismounted_explicitly()
    {
        var drive = Empty with { Volumes = [Mounted] };

        var refused = WriteGuard.Evaluate(drive, null, dismountVolumes: false);
        var allowed = WriteGuard.Evaluate(drive, null, dismountVolumes: true);

        refused.Allowed.ShouldBeFalse();
        refused.Refusals.Single().ShouldContain("X:");
        allowed.Allowed.ShouldBeTrue();
        allowed.Volumes.ShouldBe([Mounted]);
    }

    [Fact]
    public void Range_is_aligned_and_reported_in_dry_run()
    {
        var plan = WriteGuard.Evaluate(Empty, new DriveRange(1000, 5000), dismountVolumes: false);

        plan.Range.ShouldBe(new DriveRange(512, 4608));
        plan.Bytes.ShouldBe(4096UL);
    }

    [Fact]
    public void Confirmation_uses_serial_for_cli_and_model_for_gui()
    {
        WriteGuard.ExpectedConfirmation(Empty, ConfirmationKind.Serial).ShouldBe("SER-42");
        WriteGuard.ExpectedConfirmation(Empty, ConfirmationKind.Model).ShouldBe("Virtual Disk");
        WriteGuard.ExpectedConfirmation(Empty with { Serial = null }, ConfirmationKind.Serial).ShouldBe("Virtual Disk");
    }

    [Fact]
    public void Authorize_refuses_wrong_confirmation_and_system_disk()
    {
        var guard = new WriteGuard(new FakeDrives(Empty with { IsSystemDisk = true }));

        var ex = Should.Throw<WriteRefusedException>(() => guard.Authorize(Empty.Id, null, false, "falsch", ConfirmationKind.Serial));

        ex.Reasons.ShouldContain(r => r.Contains("System", StringComparison.Ordinal));
        ex.Reasons.ShouldContain(r => r.Contains("Seriennummer", StringComparison.Ordinal));
    }

    [Fact]
    public void Authorize_rereads_the_drive()
    {
        var drives = new FakeDrives(Empty);
        var guard = new WriteGuard(drives);
        drives.Current = Empty with { Volumes = [Mounted] };

        Should.Throw<WriteRefusedException>(() => guard.Authorize(Empty.Id, null, false, "SER-42", ConfirmationKind.Serial))
            .Reasons.ShouldContain(r => r.Contains("Volumes", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ErasePattern.Zero, (byte)0)]
    [InlineData(ErasePattern.Fixed, (byte)0xA5)]
    public void Erase_fills_the_whole_range_with_the_pattern(ErasePattern pattern, byte expected)
    {
        var device = new FakeWritableDevice(8 * MiB);
        device.Content.AsSpan().Fill(0x11);

        var result = Eraser.Run(device, Empty.Id, new DriveRange(MiB, 7 * MiB), new EraseOptions(pattern, 0xA5, BlockCount: 12), null, CancellationToken.None);

        result.Blocks.Count.ShouldBe(12);
        result.Blocks.ShouldAllBe(b => b == EraseBlockState.Written);
        result.BytesWritten.ShouldBe(6 * MiB);
        device.Content.AsSpan((int)MiB, (int)(6 * MiB)).ToArray().ShouldAllBe(b => b == expected);
        device.Content.AsSpan(0, (int)MiB).ToArray().ShouldAllBe(b => b == 0x11);
        device.Content.AsSpan((int)(7 * MiB)).ToArray().ShouldAllBe(b => b == 0x11);
    }

    [Fact]
    public void Erase_runs_all_passes_with_different_random_data()
    {
        var device = new FakeWritableDevice(2 * MiB);

        var result = Eraser.Run(device, Empty.Id, new DriveRange(0, 2 * MiB), new EraseOptions(ErasePattern.Random, Passes: 3, BlockCount: 2, Seed: 5), null, CancellationToken.None);

        result.BytesWritten.ShouldBe(6 * MiB);
        device.Writes.Count.ShouldBe(6);
        device.Content.Distinct().Count().ShouldBeGreaterThan(200);
    }

    [Fact]
    public void Verify_detects_corrupted_and_failing_blocks()
    {
        var device = new FakeWritableDevice(4 * MiB);
        device.CorruptReadBackAt.Add(MiB);
        device.FailWriteAt.Add(3 * MiB);

        var result = Eraser.Run(device, Empty.Id, new DriveRange(0, 4 * MiB), new EraseOptions(Verify: true, BlockCount: 4), null, CancellationToken.None);

        result.Errors.ShouldBe(2);
        result.Blocks.ShouldBe([EraseBlockState.Written, EraseBlockState.Error, EraseBlockState.Written, EraseBlockState.Error]);
    }

    [Fact]
    public void Erase_can_be_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var device = new FakeWritableDevice(16 * MiB) { OnWrite = count => { if (count == 3) { cts.Cancel(); } } };

        Should.Throw<OperationCanceledException>(
            () => Eraser.Run(device, Empty.Id, new DriveRange(0, 16 * MiB), new EraseOptions(), null, cts.Token));
        device.Writes.Count.ShouldBe(3);
    }

    [Fact]
    public void Write_adapter_turns_measurement_reads_into_writes()
    {
        var device = new FakeWritableDevice(64 * MiB);
        var adapter = new WriteAdapter(device, 65536);

        var result = BenchmarkRunner.Run(adapter, Empty.Id, new BenchmarkOptions(Points: 4, BlocksPerPoint: 2, AccessSamples: 5, BurstRepeats: 2, Seed: 1), null, CancellationToken.None);

        result.Transfer.Count.ShouldBe(4);
        device.Writes.Count.ShouldBeGreaterThan(8);
        device.Reads.ShouldBe(0);
    }

    [Theory]
    [InlineData(0x42, 0x80, true)]
    [InlineData(0x42, 0xFE, true)]
    [InlineData(0x42, 0x7F, false)]
    [InlineData(0x42, 0xFF, false)]
    [InlineData(0xC2, 0x00, true)]
    [InlineData(0x05, 0x01, true)]
    [InlineData(0x05, 0x00, false)]
    [InlineData(0x85, 0x00, true)]
    [InlineData(0x02, 0x00, false)] // enable write cache: not ours to change
    [InlineData(0x82, 0x00, false)] // disable write cache
    public void Only_aam_and_apm_settings_are_allowed(byte feature, byte level, bool allowed) =>
        Native.AtaCommands.IsAcousticPowerSetting(Native.AtaCommands.SetFeature(feature, level)).ShouldBe(allowed);

    [Fact]
    public void Aam_apm_require_confirmation_before_touching_the_drive() =>
        Should.Throw<WriteRefusedException>(() => new AcousticPowerService().SetApm(Empty, 128, confirmed: false));

    private sealed class FakeDrives(DriveInfo drive) : IDriveService
    {
        public DriveInfo Current { get; set; } = drive;

        public IReadOnlyList<DriveId> ListDrives() => [Current.Id];

        public IReadOnlyList<DriveInfo> GetDrives() => [Current];

        public DriveInfo GetDrive(DriveId id) => Current;
    }
}

/// <summary>In-memory writable device.</summary>
internal sealed class FakeWritableDevice(ulong size) : IWritableBlockDevice
{
    public byte[] Content { get; } = new byte[size];

    public List<(ulong Offset, int Length)> Writes { get; } = [];

    public int Reads { get; private set; }

    public HashSet<ulong> CorruptReadBackAt { get; } = [];

    public HashSet<ulong> FailWriteAt { get; } = [];

    public Action<int>? OnWrite { get; set; }

    public ulong Size { get; } = size;

    public uint SectorSize => 512;

    public int Read(ulong offset, int length)
    {
        Reads++;
        return length;
    }

    public void Write(ulong offset, ReadOnlySpan<byte> data)
    {
        Writes.Add((offset, data.Length));
        OnWrite?.Invoke(Writes.Count);
        if (FailWriteAt.Contains(offset))
        {
            throw new System.IO.IOException("Simulierter Schreibfehler");
        }

        data.CopyTo(Content.AsSpan((int)offset));
    }

    public ReadOnlySpan<byte> ReadBack(ulong offset, int length)
    {
        var copy = Content.AsSpan((int)offset, length).ToArray();
        if (CorruptReadBackAt.Contains(offset))
        {
            copy[0] ^= 0xFF;
        }

        return copy;
    }

    public void Dispose()
    {
    }
}
