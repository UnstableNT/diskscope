using DiskScope.Core.Devices;

namespace DiskScope.Core.Smart;

public static class TemperatureLimits
{
    /// <summary>Default warning threshold (SPEC 4.12): 50 °C for HDDs, 70 °C for SSDs.</summary>
    public static int DefaultWarningCelsius(MediaKind media) => media == MediaKind.Hdd ? 50 : 70;

    public static double ToFahrenheit(int celsius) => (celsius * 9.0 / 5.0) + 32;
}
