using System.Collections.Concurrent;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskScope.Core.Monitoring;

/// <summary>
/// Real-time disk I/O of one drive from the ETW kernel provider (SPEC 4.9). Needs admin rights.
/// The session has a fixed name; an orphaned session of the same name (e.g. after a crash) is stopped first.
/// </summary>
public sealed partial class DiskMonitor(ILogger<DiskMonitor>? logger = null)
{
    public const string SessionName = "DiskScope-DiskIO";

    private readonly ILogger _logger = logger ?? NullLogger<DiskMonitor>.Instance;

    /// <summary>Feeds events of <paramref name="diskNumber"/> into <paramref name="statistics"/> until cancelled.</summary>
    public Task RunAsync(int diskNumber, MonitorStatistics statistics, CancellationToken cancellationToken)
    {
        if (!Privileges.IsElevated)
        {
            throw new DiskScopeException("ETW-Kernelsitzung", DiskScopeException.ErrorAccessDenied);
        }

        return Task.Factory.StartNew(
            () => Run(diskNumber, statistics, cancellationToken),
            cancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private void Run(int diskNumber, MonitorStatistics statistics, CancellationToken cancellationToken)
    {
        StopOrphanedSession();
        using var session = new TraceEventSession(SessionName) { StopOnDispose = true };
        session.EnableKernelProvider(
            KernelTraceEventParser.Keywords.DiskIO | KernelTraceEventParser.Keywords.DiskIOInit | KernelTraceEventParser.Keywords.Process);
        LogStarted(diskNumber);

        // DiskIoInit is logged in the context of the issuing thread, the completion often is not:
        // remember the issuing process per IRP and attach it to the completion event.
        var issuers = new ConcurrentDictionary<ulong, (int Pid, string Name)>();
        var kernel = session.Source.Kernel;
        kernel.DiskIOReadInit += e => issuers[e.Irp] = (e.ProcessID, e.ProcessName);
        kernel.DiskIOWriteInit += e => issuers[e.Irp] = (e.ProcessID, e.ProcessName);
        kernel.DiskIORead += e => OnCompleted(e, isWrite: false);
        kernel.DiskIOWrite += e => OnCompleted(e, isWrite: true);

        using var registration = cancellationToken.Register(() => session.Stop());
        session.Source.Process();
        LogStopped(diskNumber);
        cancellationToken.ThrowIfCancellationRequested();

        void OnCompleted(DiskIOTraceData e, bool isWrite)
        {
            var issuer = issuers.TryRemove(e.Irp, out var known) ? known : (e.ProcessID, e.ProcessName);
            if (e.DiskNumber != diskNumber)
            {
                return;
            }

            statistics.Add(new DiskIoEvent(
                e.TimeStamp,
                isWrite,
                e.ByteOffset,
                e.TransferSize,
                issuer.Item1,
                string.IsNullOrEmpty(issuer.Item2) ? (issuer.Item1 <= 4 ? "System" : $"PID {issuer.Item1}") : issuer.Item2));
        }
    }

    private void StopOrphanedSession()
    {
        if (TraceEventSession.GetActiveSessionNames().Contains(SessionName))
        {
            using var orphan = new TraceEventSession(SessionName, TraceEventSessionOptions.Attach);
            orphan.Stop();
            LogOrphanStopped();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Monitor started for drive {Drive}")]
    private partial void LogStarted(int drive);

    [LoggerMessage(Level = LogLevel.Information, Message = "Monitor stopped for drive {Drive}")]
    private partial void LogStopped(int drive);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Orphaned ETW session stopped")]
    private partial void LogOrphanStopped();
}
