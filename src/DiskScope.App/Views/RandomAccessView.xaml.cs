using DiskScope.App.Controls;
using DiskScope.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using ScottPlot.Plottables;

namespace DiskScope.App.Views;

/// <summary>Scatter plot of access time over position, one color per transfer size (SPEC 4.10).</summary>
public sealed partial class RandomAccessView : UserControl, IViewWithResults
{
    private readonly Dictionary<RandomSeries, Scatter> _plotted = [];
    private readonly DispatcherQueueTimer _redraw;
    private bool _dirty = true;

    public RandomAccessView()
    {
        ViewModel = App.Services.GetRequiredService<RandomAccessViewModel>();
        InitializeComponent();

        ChartStyle.Apply(Chart.Plot);
        Chart.Plot.Axes.Bottom.Label.Text = "Position (GB)";
        Chart.Plot.Axes.Left.Label.Text = "Zugriffszeit (ms)";

        _redraw = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _redraw.Interval = TimeSpan.FromMilliseconds(150);
        _redraw.Tick += (_, _) => Redraw();
        _redraw.Start();
        ViewModel.SamplesChanged += (_, _) => _dirty = true;
    }

    public RandomAccessViewModel ViewModel { get; }

    public IResultText Results => ViewModel;

    private void Redraw()
    {
        if (!_dirty)
        {
            return;
        }

        _dirty = false;
        var plot = Chart.Plot;

        // Series are replaced per run; keep the plottables in sync.
        foreach (var stale in _plotted.Keys.Where(s => !ViewModel.Series.Contains(s)).ToArray())
        {
            plot.Remove(_plotted[stale]);
            _plotted.Remove(stale);
        }

        foreach (var series in ViewModel.Series.Where(s => !_plotted.ContainsKey(s)))
        {
            var scatter = plot.Add.Scatter(series.X, series.Y, ScottPlot.Color.FromHex(series.Color));
            scatter.LineWidth = 0;
            scatter.MarkerSize = 4;
            scatter.LegendText = series.Label;
            _plotted[series] = scatter;
        }

        plot.ShowLegend(ScottPlot.Alignment.UpperRight);
        var gb = (ViewModel.Selection.Current?.SizeBytes ?? 1_000_000_000) / 1e9;
        plot.Axes.SetLimitsX(0, gb);
        var maxMs = ViewModel.Series.Where(s => s.Y.Count > 0).Select(s => s.Y.Max()).DefaultIfEmpty(1).Max();
        plot.Axes.SetLimitsY(0, maxMs * 1.15);
        Chart.Refresh();
    }
}
