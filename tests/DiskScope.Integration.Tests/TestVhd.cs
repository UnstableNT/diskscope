using System.Diagnostics;
using System.Globalization;
using DiskScope.Core.Devices;

namespace DiskScope.Integration.Tests;

/// <summary>
/// A fixed-size VHDX created and attached with diskpart, without drive letter. All raw-device tests run
/// against such disks only (SPEC 9, 11). Requires admin rights.
/// </summary>
internal sealed class TestVhd : IDisposable
{
    private TestVhd(string path, DriveId drive)
    {
        Path = path;
        Drive = drive;
    }

    public string Path { get; }

    public DriveId Drive { get; }

    public static TestVhd Create(int sizeMegabytes)
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DiskScopeTests");
        System.IO.Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, $"diskscope-{Guid.NewGuid():N}.vhdx");

        RunDiskpart(
            $"create vdisk file=\"{path}\" maximum={sizeMegabytes.ToString(CultureInfo.InvariantCulture)} type=fixed",
            $"select vdisk file=\"{path}\"",
            "attach vdisk noerr");

        try
        {
            var number = Run(
                "powershell.exe",
                $"-NoProfile -Command \"(Get-DiskImage -ImagePath '{path}' | Get-Disk).Number\"").Trim();
            return new TestVhd(path, new DriveId(int.Parse(number, CultureInfo.InvariantCulture)));
        }
        catch
        {
            Detach(path);
            throw;
        }
    }

    public void Dispose() => Detach(Path);

    /// <summary>Creates an NTFS partition with a drive letter, i.e. a mounted volume.</summary>
    public void CreateMountedVolume() =>
        RunDiskpart(
            $"select vdisk file=\"{Path}\"",
            "convert gpt noerr",
            "create partition primary",
            "format fs=ntfs quick label=DSTEST",
            "assign");

    private static void Detach(string path)
    {
        try
        {
            RunDiskpart($"select vdisk file=\"{path}\"", "detach vdisk noerr");
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static void RunDiskpart(params string[] commands)
    {
        var script = System.IO.Path.GetTempFileName();
        try
        {
            System.IO.File.WriteAllLines(script, commands);
            Run("diskpart.exe", $"/s \"{script}\"");
        }
        finally
        {
            System.IO.File.Delete(script);
        }
    }

    private static string Run(string file, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{file} {arguments} fehlgeschlagen ({process.ExitCode}): {output}");
        }

        return output;
    }
}
