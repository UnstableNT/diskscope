using DiskScope.App.ViewModels;
using DiskScope.Core.Destructive;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace DiskScope.App.Views;

public sealed partial class EraseView : UserControl, IViewWithResults
{
    private const float TargetCell = 12f;
    private const float Gap = 2f;

    private static readonly Color Written = Color.FromArgb(255, 0x4C, 0xC2, 0xFF);
    private static readonly Color Failed = Color.FromArgb(255, 0xF8, 0x51, 0x49);
    private static readonly Color Open = Color.FromArgb(255, 0x30, 0x36, 0x3D);

    private readonly DispatcherQueueTimer _redraw;
    private bool _dirty = true;

    public EraseView()
    {
        ViewModel = App.Services.GetRequiredService<EraseViewModel>();
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

    public EraseViewModel ViewModel { get; }

    public IResultText Results => ViewModel;

    private void OnGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ViewModel.GridCapacity = Math.Max(1, (int)(e.NewSize.Width / TargetCell)) * Math.Max(1, (int)(e.NewSize.Height / TargetCell));
        _dirty = true;
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var blocks = ViewModel.Blocks;
        var count = blocks.Length > 0 ? blocks.Length : ViewModel.GridCapacity;
        var (columns, cell) = ScanView.Layout(count, (float)sender.ActualWidth, (float)sender.ActualHeight);
        var size = Math.Max(1f, cell - Gap);
        for (var i = 0; i < count; i++)
        {
            var state = i < blocks.Length ? blocks[i] : EraseBlockState.Open;
            var color = state switch
            {
                EraseBlockState.Written => Written,
                EraseBlockState.Error => Failed,
                _ => Open,
            };
            args.DrawingSession.FillRectangle((i % columns) * cell, (i / columns) * cell, size, size, color);
        }
    }
}
