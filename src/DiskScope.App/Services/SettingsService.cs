using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace DiskScope.App.Services;

public enum TemperatureUnit { Celsius, Fahrenheit }

/// <summary>User settings, stored as JSON in %APPDATA%\DiskScope\settings.json (SPEC 6).</summary>
public sealed record AppSettings
{
    public TemperatureUnit TemperatureUnit { get; init; } = TemperatureUnit.Celsius;

    public int TemperatureIntervalSeconds { get; init; } = 60;

    public int HddWarningCelsius { get; init; } = 50;

    public int SsdWarningCelsius { get; init; } = 70;

    public int SmartRefreshMinutes { get; init; } = 5;

    public string BenchmarkBlockSize { get; init; } = "64K";

    /// <summary>Save a screenshot of the tab after every completed test (SPEC 4.15).</summary>
    public bool AutoScreenshot { get; init; }

    public string ScreenshotFolder { get; init; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "DiskScope");
}

public sealed partial class SettingsService(ILogger<SettingsService> logger)
{
    private static readonly string FilePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DiskScope", "settings.json");

    private AppSettings? _current;

    public event EventHandler? Changed;

    public AppSettings Current => _current ??= Load();

    public void Save(AppSettings settings)
    {
        _current = settings;
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            System.IO.File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings));
        }
        catch (System.IO.IOException ex)
        {
            LogSaveFailed(ex);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private AppSettings Load()
    {
        try
        {
            return System.IO.File.Exists(FilePath)
                ? JsonSerializer.Deserialize(System.IO.File.ReadAllText(FilePath), SettingsJsonContext.Default.AppSettings) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or System.IO.IOException)
        {
            LogLoadFailed(ex);
            return new AppSettings();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Einstellungen konnten nicht gelesen werden")]
    private partial void LogLoadFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Einstellungen konnten nicht gespeichert werden")]
    private partial void LogSaveFailed(Exception exception);
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
