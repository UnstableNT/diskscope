using System.ComponentModel;
using System.Runtime.InteropServices.WindowsRuntime;
using DiskScope.App.Services;
using DiskScope.App.ViewModels;
using DiskScope.App.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Pickers;

namespace DiskScope.App;

public sealed partial class MainWindow : Window
{
    private readonly Dictionary<string, FrameworkElement> _tabs = [];

    public MainWindow()
    {
        ViewModel = App.Services.GetRequiredService<ShellViewModel>();
        InitializeComponent();
        var workArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(Math.Min(1500, workArea.Width * 9 / 10), Math.Min(980, workArea.Height * 9 / 10)));
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "DiskScope.ico"));
        TitleBarTheme.Apply(this, AppTitleBar);

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        TestViewModelBase[] tests =
        [
            App.Services.GetRequiredService<BenchmarkViewModel>(),
            App.Services.GetRequiredService<ScanViewModel>(),
            App.Services.GetRequiredService<RandomAccessViewModel>(),
            App.Services.GetRequiredService<FileBenchmarkViewModel>(),
            App.Services.GetRequiredService<ExtraTestsViewModel>(),
            App.Services.GetRequiredService<FolderUsageViewModel>(),
        ];
        foreach (var test in tests)
        {
            test.Completed += OnTestCompleted;
        }
        Navigation.SelectedItem = Navigation.MenuItems[0];
        _ = LoadAsync();
    }

    public ShellViewModel ViewModel { get; }

    private async Task LoadAsync()
    {
        await ViewModel.LoadAsync().ConfigureAwait(true);
        DriveSelector.SelectedItem = ViewModel.SelectedDrive;
        if (ViewModel.ErrorText is { } error)
        {
            ShowMessage("Laufwerke konnten nicht gelesen werden", error, InfoBarSeverity.Error);
        }
    }

    private void OnDriveSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DriveSelector.SelectedItem is DriveItem item && !ReferenceEquals(item, ViewModel.SelectedDrive))
        {
            ViewModel.SelectedDrive = item;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.TemperatureLevel))
        {
            TemperatureIcon.Foreground = (Brush)Application.Current.Resources[ViewModel.TemperatureLevel switch
            {
                TemperatureLevel.Normal => "StatusOkBrush",
                TemperatureLevel.Warning => "StatusWarningBrush",
                TemperatureLevel.Critical => "StatusErrorBrush",
                _ => "StatusUnknownBrush",
            }];
        }
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string tag })
        {
            return;
        }

        if (!_tabs.TryGetValue(tag, out var view))
        {
            view = tag switch
            {
                "Benchmark" => new BenchmarkView(),
                "Info" => new InfoView(),
                "Health" => new HealthView(),
                "Scan" => new ScanView(),
                "Random" => new RandomAccessView(),
                "FileBench" => new FileBenchmarkView(),
                "Folders" => new FolderUsageView(),
                "Monitor" => new MonitorView(),
                "Erase" => new EraseView(),
                "Extra" => new ExtraTestsView(),
                _ => new PlaceholderView((string)((NavigationViewItem)args.SelectedItem).Content),
            };
            _tabs[tag] = view;
        }

        TabHost.Content = view;
    }

    private IResultText? CurrentResults => (TabHost.Content as IViewWithResults)?.Results;

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (CurrentResults?.ResultText is not { } text)
        {
            ShowMessage("Nichts zu kopieren", "Dieser Tab hat noch keine Ergebnisse.", InfoBarSeverity.Informational);
            return;
        }

        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        ShowMessage("Kopiert", "Die Ergebnisse liegen in der Zwischenablage.", InfoBarSeverity.Success);
    }

    private async void OnScreenshotClick(object sender, RoutedEventArgs e)
    {
        if (TabHost.Content is not UIElement content)
        {
            return;
        }

        var picker = new FileSavePicker { SuggestedFileName = SuggestedName(), DefaultFileExtension = ".png" };
        picker.FileTypeChoices.Add("PNG-Bild", [".png"]);
        picker.FileTypeChoices.Add("JPEG-Bild", [".jpg"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite);
        await RenderAsync(content, stream, file.FileType.Equals(".jpg", StringComparison.OrdinalIgnoreCase));
        ShowMessage("Screenshot gespeichert", file.Path, InfoBarSeverity.Success);
    }

    private async Task RenderAsync(UIElement content, Windows.Storage.Streams.IRandomAccessStream stream, bool jpeg)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(content);
        var pixels = await bitmap.GetPixelsAsync();
        var encoder = await BitmapEncoder.CreateAsync(jpeg ? BitmapEncoder.JpegEncoderId : BitmapEncoder.PngEncoderId, stream);
        var dpi = 96 * (float)(Content.XamlRoot?.RasterizationScale ?? 1);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, dpi, dpi, pixels.ToArray());
        await encoder.FlushAsync();
    }

    /// <summary>Automatic screenshot after a test, pattern {modell}_{test}_{datum}.png (SPEC 4.15).</summary>
    private async void OnTestCompleted(object? sender, EventArgs e)
    {
        var settings = App.Services.GetRequiredService<SettingsService>().Current;
        if (!settings.AutoScreenshot || TabHost.Content is not UIElement content || !ReferenceEquals(CurrentResults, sender))
        {
            return;
        }

        try
        {
            // Let throttled charts draw their final state first.
            await Task.Delay(400).ConfigureAwait(true);
            System.IO.Directory.CreateDirectory(settings.ScreenshotFolder);
            var path = System.IO.Path.Combine(settings.ScreenshotFolder, SuggestedName() + ".png");
            using var file = System.IO.File.Create(path);
            await RenderAsync(content, file.AsRandomAccessStream(), jpeg: false);
            ShowMessage("Screenshot automatisch gespeichert", path, InfoBarSeverity.Success);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            ShowMessage("Automatischer Screenshot fehlgeschlagen", ex.Message, InfoBarSeverity.Warning);
        }
    }

    /// <summary>Saves the current tab's full result as JSON (SPEC 4.15).</summary>
    private async void OnJsonClick(object sender, RoutedEventArgs e)
    {
        if (CurrentResults?.ResultJson is not { } json)
        {
            ShowMessage("Nichts zu exportieren", "Dieser Tab hat noch keine Ergebnisse.", InfoBarSeverity.Informational);
            return;
        }

        var picker = new FileSavePicker { SuggestedFileName = SuggestedName(), DefaultFileExtension = ".json" };
        picker.FileTypeChoices.Add("JSON", [".json"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        if (await picker.PickSaveFileAsync() is { } file)
        {
            await System.IO.File.WriteAllTextAsync(file.Path, json).ConfigureAwait(true);
            ShowMessage("JSON gespeichert", file.Path, InfoBarSeverity.Success);
        }
    }

    private string SuggestedName()
    {
        var model = ViewModel.Selection.Current?.Model ?? "DiskScope";
        var tab = (Navigation.SelectedItem as NavigationViewItem)?.Content as string ?? "Tab";
        var name = $"{model}_{tab}_{DateTime.Now:yyyy-MM-dd_HHmm}";
        return string.Concat(name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));
    }

    private StatusWindow? _statusWindow;

    private void OnStatusClick(object sender, RoutedEventArgs e)
    {
        if (_statusWindow is null)
        {
            _statusWindow = new StatusWindow();
            _statusWindow.Closed += (_, _) => _statusWindow = null;
        }

        _statusWindow.Activate();
    }

    private async void OnAboutClick(object sender, RoutedEventArgs e) =>
        await new AboutDialog { XamlRoot = Content.XamlRoot }.ShowAsync();

    private async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog(App.Services.GetRequiredService<SettingsService>()) { XamlRoot = Content.XamlRoot };
        await dialog.ShowAsync();
    }

    private void ShowMessage(string title, string message, InfoBarSeverity severity)
    {
        MessageBar.Title = title;
        MessageBar.Message = message;
        MessageBar.Severity = severity;
        MessageBar.IsOpen = true;
    }
}

/// <summary>A tab view whose results "Ergebnisse kopieren" can copy.</summary>
public interface IViewWithResults
{
    IResultText Results { get; }
}
