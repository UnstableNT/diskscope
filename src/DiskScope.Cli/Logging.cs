using Microsoft.Extensions.Logging;
using Serilog;

namespace DiskScope.Cli;

/// <summary>Rolling log files in %APPDATA%\DiskScope\logs, optionally mirrored to stderr (SPEC 8).</summary>
internal static class Logging
{
    public static ILoggerFactory Create(bool verbose)
    {
        var directory = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DiskScope", "logs");

        var serilog = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                System.IO.Path.Combine(directory, "diskscope-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                formatProvider: System.Globalization.CultureInfo.InvariantCulture)
            .CreateLogger();

        return LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddSerilog(serilog, dispose: true);
            if (verbose)
            {
                // stderr keeps --json output on stdout clean.
                builder.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
            }
        });
    }
}
