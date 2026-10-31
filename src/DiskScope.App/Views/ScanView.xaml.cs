using DiskScope.App.ViewModels;
using DiskScope.Core.Scanning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using Windows.UI;

namespace DiskScope.App.Views;

/// <summary>Error scan with a Win2D block grid that copes with thousands of tiles (SPEC 4.5, 6).</summary>
public sealed partial class ScanView : UserControl, IViewWithResults
{
    private const float TargetCell = 12f;
    private const float Gap = 2f;

    private static readonly Color Good = Color.FromArgb(255, 0x3F, 0xB9, 0x50);
    private static readonly Color Bad = Color.FromArgb(255, 0xF8, 0x51, 0x49);
    private static readonly Color BadOnSpeedMap = Color.FromArgb(255, 0x82, 0x50, 0xDF);
    private static readonly Color Unchecked = Color.FromArgb(255, 0x30, 0x36, 0x3D);
    private static readonly Color Slow = Bad;
    private static readonly Color Medium = Color.FromArgb(255, 0xD2, 0x99, 0x22);

    private readonly DispatcherQueueTimer _redraw;
    private bool _dirty;

    public ScanView()
    {
        ViewModel = App.Services.GetRequiredService<ScanViewModel>();
        InitializeComponent();

        _redraw = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _redraw.Interval = TimeSpan.FromMilliseconds(100);
        _redraw.Tick += (_, _) =>
        {
            if (_dirty)
            {
                _dirty = false;
                BlockCanvas.Invalidate();
            }
        };
        _redraw.Start();
        ViewModel.BlocksChanged += (_, _) => _dirty = true;
    }

    public ScanViewModel ViewModel { get; }

    public IResultText Results => ViewModel;

    /// <summary>Columns for <paramref name="count"/> blocks so the grid fills the area with square cells.</summary>
    internal static (int Columns, float Cell) Layout(int count, float width, float height)
    {
        if (count <= 0 || width <= 0 || height <= 0)
        {
            return (1, TargetCell);
        }

        var cell = MathF.Sqrt(width * height / count);
        var columns = Math.Max(1, (int)(width / cell));
        while ((int)Math.Ceiling((double)count / columns) * (width / columns) > height && columns < count)
        {
            columns++;
        }

        return (columns, width / columns);
    }

    private void OnGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Rastergröße passt sich der Fensterfläche an (SPEC 4.5).
        var columns = Math.Max(1, (int)(e.NewSize.Width / TargetCell));
        var rows = Math.Max(1, (int)(e.NewSize.Height / TargetCell));
        ViewModel.GridCapacity = columns * rows;
        _dirty = true;
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var blocks = ViewModel.Blocks;
        var width = (float)sender.ActualWidth;
        var height = (float)sender.ActualHeight;
        var count = blocks.Length > 0 ? blocks.Length : ViewModel.GridCapacity;
        var (columns, cell) = Layout(count, width, height);
        var size = Math.Max(1f, cell - Gap);
        var speedMap = MapType.SelectedIndex == 1;
        var maxSpeed = ViewModel.MaxSpeed;

        for (var i = 0; i < count; i++)
        {
            var state = i < blocks.Length ? blocks[i] : BlockState.Unchecked;
            var color = state switch
            {
                BlockState.Damaged => speedMap ? BadOnSpeedMap : Bad,
                BlockState.Good when speedMap && maxSpeed > 0 && i < ViewModel.Speeds.Length => SpeedColor(ViewModel.Speeds[i] / maxSpeed),
                BlockState.Good => Good,
                _ => Unchecked,
            };
            args.DrawingSession.FillRectangle((i % columns) * cell, (i / columns) * cell, size, size, color);
        }
    }

    private static Color SpeedColor(double fraction)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        return fraction < 0.5 ? Lerp(Slow, Medium, fraction * 2) : Lerp(Medium, Good, (fraction - 0.5) * 2);
    }

    private static Color Lerp(Color a, Color b, double t) => Color.FromArgb(
        255, (byte)(a.R + ((b.R - a.R) * t)), (byte)(a.G + ((b.G - a.G) * t)), (byte)(a.B + ((b.B - a.B) * t)));

    private void OnMapTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is null || SpeedLegend is null)
        {
            return;
        }

        var speed = MapType.SelectedIndex == 1;
        ViewModel.SpeedMap = speed;
        SpeedLegend.Visibility = speed ? Visibility.Visible : Visibility.Collapsed;
        ErrorLegend.Visibility = speed ? Visibility.Collapsed : Visibility.Visible;
        _dirty = true;
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Result is not { } result)
        {
            return;
        }

        var picker = new FileSavePicker { SuggestedFileName = $"defekte-lbas_{DateTime.Now:yyyy-MM-dd_HHmm}" };
        picker.FileTypeChoices.Add("CSV", [".csv"]);
        picker.FileTypeChoices.Add("JSON", [".json"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        if (await picker.PickSaveFileAsync() is { } file)
        {
            ScanExport.Write(result, file.Path);
            ViewModel.Status = $"Exportiert nach {file.Path}";
        }
    }
}
