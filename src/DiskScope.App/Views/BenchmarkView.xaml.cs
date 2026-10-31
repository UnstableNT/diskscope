using DiskScope.App.Controls;
using DiskScope.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using ScottPlot.Plottables;

namespace DiskScope.App.Views;

public sealed partial class BenchmarkView : UserControl, IViewWithResults
{
    private readonly Scatter _transfer;
    private readonly Scatter _access;
    private readonly DispatcherQueueTimer _redraw;
    private bool _dirty = true;

    public BenchmarkView()
    {
        ViewModel = App.Services.GetRequiredService<BenchmarkViewModel>();
        InitializeComponent();

        var plot = Chart.Plot;
        ChartStyle.Apply(plot);
        plot.Axes.Bottom.Label.Text = "Position (GB)";
        plot.Axes.Left.Label.Text = "Transferrate (MB/s)";
        plot.Axes.Right.Label.Text = "Zugriffszeit (ms)";

        _transfer = plot.Add.Scatter(ViewModel.TransferX, ViewModel.TransferY, ChartStyle.Transfer);
        _transfer.MarkerSize = 0;
        _transfer.LineWidth = 2;
        _transfer.LegendText = "Transferrate";

        _access = plot.Add.Scatter(ViewModel.AccessX, ViewModel.AccessY, ChartStyle.Access);
        _access.LineWidth = 0;
        _access.MarkerSize = 4;
        _access.Axes.YAxis = plot.Axes.Right;
        _access.LegendText = "Zugriffszeit";
        plot.ShowLegend(ScottPlot.Alignment.LowerRight);

        // Charts are redrawn at most ten times per second while samples arrive.
        _redraw = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _redraw.Interval = TimeSpan.FromMilliseconds(100);
        _redraw.Tick += (_, _) => Redraw();
        _redraw.Start();
        ViewModel.SamplesChanged += (_, _) => _dirty = true;
        ViewModel.PropertyChanged += (_, _) => _dirty = true;
    }

    public BenchmarkViewModel ViewModel { get; }

    public IResultText Results => ViewModel;

    private void Redraw()
    {
        if (!_dirty)
        {
            return;
        }

        _dirty = false;
        var plot = Chart.Plot;
        _transfer.IsVisible = ViewModel.MeasureTransfer && ViewModel.TransferX.Count > 0;
        _access.IsVisible = ViewModel.MeasureAccess && ViewModel.AccessX.Count > 0;

        var maxX = ViewModel.DriveGigabytes > 0 ? ViewModel.DriveGigabytes : 1;
        plot.Axes.SetLimitsX(0, maxX);
        var maxRate = ViewModel.TransferY.Count > 0 ? ViewModel.TransferY.Max() : 100;
        plot.Axes.SetLimitsY(0, maxRate * 1.15, plot.Axes.Left);
        var maxMs = ViewModel.AccessY.Count > 0 ? ViewModel.AccessY.Max() : 1;
        plot.Axes.SetLimitsY(0, maxMs * 1.15, plot.Axes.Right);
        Chart.Refresh();
    }
}
