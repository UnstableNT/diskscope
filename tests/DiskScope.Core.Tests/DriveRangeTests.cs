using DiskScope.Core.Benchmarks;

namespace DiskScope.Core.Tests;

public class DriveRangeTests
{
    private const ulong Size = 1_000_000_000_000;

    [Theory]
    [InlineData("0-100", 0UL, Size)]
    [InlineData("10-20", 100_000_000_000UL, 200_000_000_000UL)]
    [InlineData("10%-20%", 100_000_000_000UL, 200_000_000_000UL)]
    [InlineData("10GB-20GB", 10_000_000_000UL, 20_000_000_000UL)]
    [InlineData("990GB-2000GB", 990_000_000_000UL, Size)]
    public void Parses_ranges(string text, ulong start, ulong end) =>
        DriveRange.Parse(text, Size).ShouldBe(new DriveRange(start, end));

    [Theory]
    [InlineData("50-10")]
    [InlineData("0-101")]
    [InlineData("abc")]
    [InlineData("20GB-10GB")]
    public void Rejects_invalid_ranges(string text) => Should.Throw<ArgumentException>(() => DriveRange.Parse(text, Size));

    [Fact]
    public void Aligns_to_sectors() =>
        new DriveRange(1000, 9000).AlignTo(4096, Size).ShouldBe(new DriveRange(0, 8192));

    [Theory]
    [InlineData("512", 512)]
    [InlineData("4K", 4096)]
    [InlineData("64KiB", 65536)]
    [InlineData("1M", 1048576)]
    [InlineData("8MB", 8388608)]
    public void Parses_block_sizes(string text, int expected) => BlockSize.Parse(text).ShouldBe(expected);

    [Theory]
    [InlineData("256")]
    [InlineData("16M")]
    [InlineData("3K")]
    [InlineData("x")]
    public void Rejects_invalid_block_sizes(string text) => Should.Throw<ArgumentException>(() => BlockSize.Parse(text));
}
