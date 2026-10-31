using DiskScope.App.Controls;
using DiskScope.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using ScottPlot.Plottables;

namespace DiskScope.App.Views;

public sealed partial class ExtraTestsView : UserControl, IViewWithResults
{
    private readonly Scatter _cache;

    public ExtraTestsView()
    {
        ViewModel = App.Services.GetRequiredService<ExtraTestsViewModel>();
        InitializeComponent();

        ChartStyle.Apply(Chart.Plot);
        Chart.Plot.Axes.Bottom.Label.Text = "Bereichsgröße (MiB)";
        Chart.Plot.Axes.Left.Label.Text = "MB/s";
        _cache = Chart.Plot.Add.Scatter(ViewModel.CacheX, ViewModel.CacheY, ChartStyle.Transfer);
        _cache.LineWidth = 2;
        _cache.MarkerSize = 6;
        ViewModel.CacheChanged += (_, _) => Redraw();
    }

    public ExtraTestsViewModel ViewModel { get; }

    public IResultText Results => ViewModel;

    private void Redraw()
    {
        _cache.IsVisible = ViewModel.CacheX.Count > 0;
        Chart.Plot.Axes.SetLimits(0, 66, 0, Math.Max(1, ViewModel.CacheY.DefaultIfEmpty(0).Max()) * 1.15);
        Chart.Refresh();
    }
}
