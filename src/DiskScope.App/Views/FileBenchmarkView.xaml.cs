using DiskScope.App.Controls;
using DiskScope.App.ViewModels;
using DiskScope.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScottPlot.Plottables;

namespace DiskScope.App.Views;

public sealed partial class FileBenchmarkView : UserControl, IViewWithResults
{
    private static readonly ScottPlot.Color WriteColor = ScottPlot.Color.FromHex("#FF8C64");

    private readonly Scatter _read;
    private readonly Scatter _write;
    private readonly DispatcherQueueTimer _redraw;
    private bool _dirty = true;
    private int _barsShown = -1;

    public FileBenchmarkView()
    {
        ViewModel = App.Services.GetRequiredService<FileBenchmarkViewModel>();
        InitializeComponent();

        var plot = Curves.Plot;
        ChartStyle.Apply(plot);
        plot.Axes.Bottom.Label.Text = "Dateiposition (MB)";
        plot.Axes.Left.Label.Text = "MB/s";
        _write = plot.Add.Scatter(ViewModel.WriteX, ViewModel.WriteY, WriteColor);
        _write.MarkerSize = 0;
        _write.LineWidth = 2;
        _write.LegendText = "Schreiben";
        _read = plot.Add.Scatter(ViewModel.ReadX, ViewModel.ReadY, ChartStyle.Transfer);
        _read.MarkerSize = 0;
        _read.LineWidth = 2;
        _read.LegendText = "Lesen";
        plot.ShowLegend(ScottPlot.Alignment.LowerRight);

        ChartStyle.Apply(Bars.Plot);
        Bars.Plot.Axes.Left.Label.Text = "MB/s";

        _redraw = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _redraw.Interval = TimeSpan.FromMilliseconds(150);
        _redraw.Tick += (_, _) => Redraw();
        _redraw.Start();
        ViewModel.SamplesChanged += (_, _) => _dirty = true;
    }

    public FileBenchmarkViewModel ViewModel { get; }

    public IResultText Results => ViewModel;

    private void Redraw()
    {
        if (!_dirty)
        {
            return;
        }

        _dirty = false;
        _read.IsVisible = ViewModel.ReadX.Count > 0;
        _write.IsVisible = ViewModel.WriteX.Count > 0;
        var maxX = Math.Max(1, ViewModel.WriteX.Concat(ViewModel.ReadX).DefaultIfEmpty(0).Max());
        var maxY = Math.Max(1, ViewModel.WriteY.Concat(ViewModel.ReadY).DefaultIfEmpty(0).Max());
        Curves.Plot.Axes.SetLimits(0, maxX * 1.02, 0, maxY * 1.15);
        Curves.Refresh();

        var blocks = ViewModel.BlockSizes;
        if (blocks.Count != _barsShown)
        {
            _barsShown = blocks.Count;
            BarsRow.Height = blocks.Count > 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            DrawBars(blocks);
        }
    }

    /// <summary>Grouped bars: read and write per block size (SPEC 4.8 "Blockgrößenmessung").</summary>
    private void DrawBars(IReadOnlyList<Core.Benchmarks.BlockSizeRow> blocks)
    {
        var plot = Bars.Plot;
        plot.Clear();
        if (blocks.Count == 0)
        {
            Bars.Refresh();
            return;
        }

        var read = new List<ScottPlot.Bar>();
        var write = new List<ScottPlot.Bar>();
        var ticks = new List<ScottPlot.Tick>();
        for (var i = 0; i < blocks.Count; i++)
        {
            read.Add(new ScottPlot.Bar { Position = (i * 3) + 0.5, Value = blocks[i].ReadMBps, FillColor = ChartStyle.Transfer });
            write.Add(new ScottPlot.Bar { Position = (i * 3) + 1.5, Value = blocks[i].WriteMBps, FillColor = WriteColor });
            ticks.Add(new ScottPlot.Tick((i * 3) + 1, ByteSize.FormatBinary((ulong)blocks[i].BlockSize)));
        }

        plot.Add.Bars(read).LegendText = "Lesen";
        plot.Add.Bars(write).LegendText = "Schreiben";
        plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual([.. ticks]);
        plot.ShowLegend(ScottPlot.Alignment.UpperLeft);
        plot.Axes.AutoScale();
        plot.Axes.SetLimitsY(0, blocks.Max(b => Math.Max(b.ReadMBps, b.WriteMBps)) * 1.15);
        Bars.Refresh();
    }
}
