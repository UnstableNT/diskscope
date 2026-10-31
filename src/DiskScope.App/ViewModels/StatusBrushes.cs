using DiskScope.Core.Smart;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace DiskScope.App.ViewModels;

/// <summary>Brush lookups for x:Bind function bindings.</summary>
public static class InfoViewBrushes
{
    public static Brush Feature(bool supported) => Resource(supported ? "StatusOkBrush" : "StatusUnknownBrush");

    public static Brush Health(HealthStatus status) => Resource(status switch
    {
        HealthStatus.Ok => "StatusOkBrush",
        HealthStatus.Warning => "StatusWarningBrush",
        HealthStatus.Error => "StatusErrorBrush",
        _ => "StatusUnknownBrush",
    });

    public static Brush Hex(string color)
    {
        var c = Convert.ToUInt32(color.TrimStart('#'), 16);
        return new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, (byte)(c >> 16), (byte)(c >> 8), (byte)c));
    }

    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];
}
