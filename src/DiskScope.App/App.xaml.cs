using DiskScope.App.Services;
using DiskScope.App.ViewModels;
using DiskScope.Core.Benchmarks;
using DiskScope.Core.Devices;
using DiskScope.Core.Scanning;
using DiskScope.Core.Smart;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Serilog;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace DiskScope.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        Services = ConfigureServices();
        UnhandledException += (_, e) => LogUnhandled(Services.GetRequiredService<ILogger<App>>(), e.Exception);
    }

    public static IServiceProvider Services { get; private set; } = null!;

    public static Window MainWindow => ((App)Current)._window!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();

        var status = Services.GetRequiredService<DriveStatusMonitor>();
        status.Start();
        _window.Closed += (_, _) => status.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unbehandelte Ausnahme")]
    private static partial void LogUnhandled(ILogger logger, Exception exception);

    private static ServiceProvider ConfigureServices()
    {
        var logDirectory = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DiskScope", "logs");
        var serilog = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                System.IO.Path.Combine(logDirectory, "diskscope-app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                formatProvider: System.Globalization.CultureInfo.InvariantCulture)
            .CreateLogger();

        return new ServiceCollection()
            .AddLogging(b => b.AddSerilog(serilog, dispose: true))
            .AddSingleton<SettingsService>()
            .AddSingleton<DriveSelection>()
            .AddSingleton<IDriveService, DriveService>()
            .AddSingleton<ISmartService>(sp => new SmartService(sp.GetRequiredService<ILogger<SmartService>>()))
            .AddSingleton<ISelfTestService>(sp => new SelfTestService(sp.GetRequiredService<ILogger<SelfTestService>>()))
            .AddSingleton(_ => new SmartHistory())
            .AddSingleton(sp => new BenchmarkRunner(sp.GetRequiredService<ILogger<BenchmarkRunner>>()))
            .AddSingleton(sp => new RandomAccessRunner(sp.GetRequiredService<ILogger<RandomAccessRunner>>()))
            .AddSingleton(sp => new ErrorScanner(sp.GetRequiredService<ILogger<ErrorScanner>>()))
            .AddSingleton(sp => new FileBenchmarkRunner(sp.GetRequiredService<ILogger<FileBenchmarkRunner>>()))
            .AddSingleton(sp => new ExtraTestRunner(sp.GetRequiredService<ILogger<ExtraTestRunner>>()))
            .AddSingleton(sp => new DiskScope.Core.Folders.FolderUsageScanner(sp.GetRequiredService<ILogger<DiskScope.Core.Folders.FolderUsageScanner>>()))
            .AddSingleton<FolderUsageViewModel>()
            .AddSingleton(sp => new DiskScope.Core.Monitoring.DiskMonitor(sp.GetRequiredService<ILogger<DiskScope.Core.Monitoring.DiskMonitor>>()))
            .AddSingleton<MonitorViewModel>()
            .AddSingleton<FileBenchmarkViewModel>()
            .AddSingleton<ExtraTestsViewModel>()
            .AddSingleton<DriveStatusMonitor>()
            .AddSingleton(sp => new DiskScope.Core.Destructive.WriteGuard(sp.GetRequiredService<IDriveService>()))
            .AddSingleton(sp => new DiskScope.Core.Destructive.DestructiveOperations(sp.GetRequiredService<ILogger<DiskScope.Core.Destructive.DestructiveOperations>>()))
            .AddSingleton<DiskScope.Core.Destructive.IAcousticPowerService>(sp => new DiskScope.Core.Destructive.AcousticPowerService(sp.GetRequiredService<ILogger<DiskScope.Core.Destructive.AcousticPowerService>>()))
            .AddSingleton<IWriteConfirmation, WriteConfirmation>()
            .AddSingleton<WriteSupport>()
            .AddSingleton<EraseViewModel>()
            .AddSingleton<ShellViewModel>()
            .AddSingleton<InfoViewModel>()
            .AddSingleton<HealthViewModel>()
            .AddSingleton<BenchmarkViewModel>()
            .AddSingleton<ScanViewModel>()
            .AddSingleton<RandomAccessViewModel>()
            .BuildServiceProvider();
    }
}
