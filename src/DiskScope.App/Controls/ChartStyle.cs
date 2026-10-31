using ScottPlot;

namespace DiskScope.App.Controls;

/// <summary>DiskScope chart look: dark plot area, light axes, one clear color per series (SPEC 6).</summary>
public static class ChartStyle
{
    public static readonly Color Background = Color.FromHex("#161B22");
    public static readonly Color Axis = Color.FromHex("#C9D1D9");
    public static readonly Color Grid = Color.FromHex("#30363D");
    public static readonly Color Transfer = Color.FromHex("#4CC2FF");
    public static readonly Color Access = Color.FromHex("#FFB900");

    public static void Apply(Plot plot)
    {
        plot.FigureBackground.Color = Background;
        plot.DataBackground.Color = Background;
        plot.Axes.Color(Axis);
        plot.Grid.MajorLineColor = Grid;
        plot.Legend.BackgroundColor = Color.FromHex("#21262D");
        plot.Legend.FontColor = Axis;
        plot.Legend.OutlineColor = Grid;
    }
}
