using DiskScope.App.Controls;
using DiskScope.App.ViewModels;
using DiskScope.Core.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DiskScope.App.Views;

public sealed partial class MonitorView : UserControl, IViewWithResults
{
    private static readonly ScottPlot.Color WriteColor = ScottPlot.Color.FromHex("#FF8C64");

    public MonitorView()
    {
        ViewModel = App.Services.GetRequiredService<MonitorViewModel>();
        InitializeComponent();
        foreach (var chart in new[] { RateChart, SizeChart, PositionChart })
        {
            ChartStyle.Apply(chart.Plot);
        }

        RateChart.Plot.Axes.Bottom.Label.Text = "Zeit (s)";
        RateChart.Plot.Axes.Left.Label.Text = "KB/s";
        PositionChart.Plot.Axes.Bottom.Label.Text = "Laufwerksposition (GB)";
        ViewModel.SnapshotChanged += (_, _) => Redraw();
    }

    public MonitorViewModel ViewModel { get; }

    public IResultText Results => ViewModel;

    private void Redraw()
    {
        if (ViewModel.Snapshot is not { } s)
        {
            return;
        }

        DrawRates(s);
        DrawSizes(s);
        DrawPositions(s);
    }

    private void DrawRates(MonitorSnapshot s)
    {
        var plot = RateChart.Plot;
        plot.Clear();
        if (s.Rates.Count > 0)
        {
            var start = s.Rates[0].Second;
            var x = s.Rates.Select(r => (r.Second - start).TotalSeconds).ToArray();
            var read = plot.Add.ScatterLine(x, s.Rates.Select(r => r.ReadBytes / 1024.0).ToArray(), ChartStyle.Transfer);
            read.LineWidth = 2;
            read.LegendText = "Lesen";
            var write = plot.Add.ScatterLine(x, s.Rates.Select(r => r.WriteBytes / 1024.0).ToArray(), WriteColor);
            write.LineWidth = 2;
            write.LegendText = "Schreiben";
            plot.ShowLegend(ScottPlot.Alignment.UpperLeft);
            plot.Axes.AutoScale();
            plot.Axes.SetLimitsY(0, Math.Max(10, s.Rates.Max(r => Math.Max(r.ReadBytes, r.WriteBytes)) / 1024.0) * 1.15);
        }

        RateChart.Refresh();
    }

    /// <summary>Histogram of request sizes, by number of requests or by bytes.</summary>
    private void DrawSizes(MonitorSnapshot s)
    {
        var plot = SizeChart.Plot;
        plot.Clear();
        var bytes = SizeByBytes.IsOn;
        var ticks = new List<ScottPlot.Tick>();
        var read = new List<ScottPlot.Bar>();
        var write = new List<ScottPlot.Bar>();
        for (var i = 0; i < s.Sizes.Count; i++)
        {
            var b = s.Sizes[i];
            read.Add(new ScottPlot.Bar { Position = (i * 3) + 0.5, Value = bytes ? b.ReadBytes / 1048576.0 : b.ReadCount, FillColor = ChartStyle.Transfer });
            write.Add(new ScottPlot.Bar { Position = (i * 3) + 1.5, Value = bytes ? b.WriteBytes / 1048576.0 : b.WriteCount, FillColor = WriteColor });
            ticks.Add(new ScottPlot.Tick((i * 3) + 1, b.Label));
        }

        plot.Add.Bars(read).LegendText = "Lesen";
        plot.Add.Bars(write).LegendText = "Schreiben";
        plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual([.. ticks]);
        plot.Axes.Left.Label.Text = bytes ? "MiB" : "Anzahl I/O";
        plot.ShowLegend(ScottPlot.Alignment.UpperRight);
        plot.Axes.AutoScale();
        plot.Axes.SetLimitsY(0, Math.Max(1, read.Concat(write).Max(b => b.Value)) * 1.15);
        SizeChart.Refresh();
    }

    /// <summary>Heatmap of accesses over the drive: upper row reads, lower row writes (log scale).</summary>
    private void DrawPositions(MonitorSnapshot s)
    {
        var plot = PositionChart.Plot;
        plot.Clear();
        var count = s.Positions.Count;
        var intensities = new double[2, count];
        for (var i = 0; i < count; i++)
        {
            intensities[0, i] = Math.Log10(1 + s.Positions[i].ReadCount);
            intensities[1, i] = Math.Log10(1 + s.Positions[i].WriteCount);
        }

        var heatmap = plot.Add.Heatmap(intensities);
        heatmap.Colormap = new ScottPlot.Colormaps.Turbo();
        var gigabytes = count == 0 ? 1 : s.Positions[^1].End / 1e9;
        heatmap.Extent = new ScottPlot.CoordinateRect(0, gigabytes, 0, 2);
        plot.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.NumericManual([new ScottPlot.Tick(1.5, "Lesen"), new ScottPlot.Tick(0.5, "Schreiben")]);
        plot.Axes.SetLimits(0, gigabytes, 0, 2);
        PositionChart.Refresh();
    }

    private void OnSizeModeToggled(object sender, RoutedEventArgs e) => Redraw();

    private void OnSubTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var tag = sender.SelectedItem?.Tag as string;
        SizesPanel.Visibility = tag == "Sizes" ? Visibility.Visible : Visibility.Collapsed;
        PositionPanel.Visibility = tag == "Position" ? Visibility.Visible : Visibility.Collapsed;
        ProcessesPanel.Visibility = tag == "Processes" ? Visibility.Visible : Visibility.Collapsed;
        StatisticsPanel.Visibility = tag == "Statistics" ? Visibility.Visible : Visibility.Collapsed;
    }
}
