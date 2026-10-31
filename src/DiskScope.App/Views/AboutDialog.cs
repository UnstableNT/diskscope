using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using DiskScope.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DiskScope.App.Views;

/// <summary>"Über DiskScope": developer, version, license, third-party components.</summary>
public sealed partial class AboutDialog : ContentDialog
{
    public const string Developer = "Syntaxo";

    private static readonly (string Name, string License)[] Components =
    [
        ("Windows App SDK / WinUI 3", "MIT"),
        ("Microsoft.Windows.CsWin32", "MIT"),
        ("Microsoft.Graphics.Win2D", "MIT"),
        ("CommunityToolkit.Mvvm, CommunityToolkit.WinUI", "MIT"),
        ("ScottPlot", "MIT"),
        ("Microsoft.Diagnostics.Tracing.TraceEvent", "MIT"),
        ("Microsoft.Extensions.DependencyInjection / Logging", "MIT"),
        ("Serilog, Serilog.Sinks.File", "Apache-2.0"),
        ("System.CommandLine (CLI)", "MIT"),
    ];

    public AboutDialog()
    {
        Title = "Über DiskScope";
        CloseButtonText = "Schließen";
        SecondaryButtonText = "Protokollordner öffnen";
        DefaultButton = ContentDialogButton.Close;
        SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            var logs = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DiskScope", "logs");
            System.IO.Directory.CreateDirectory(logs);
            Process.Start(new ProcessStartInfo(logs) { UseShellExecute = true });
        };

        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        header.Children.Add(new Image
        {
            Width = 64,
            Height = 64,
            Source = new BitmapImage(new Uri("ms-appx:///Assets/DiskScope.png")),
        });

        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        titles.Children.Add(new TextBlock { Text = "DiskScope", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
        titles.Children.Add(new TextBlock { Text = $"Version {Version}", Foreground = secondary });
        titles.Children.Add(new TextBlock { Text = $"Entwickelt von {Developer}" });
        header.Children.Add(titles);

        var body = new StackPanel { Spacing = 12, Width = 480 };
        body.Children.Add(header);
        body.Children.Add(new TextBlock
        {
            Text = "Analyse, Bewertung und Überwachung von Laufwerken: Benchmarks, S.M.A.R.T./NVMe-Gesundheit, Fehlerscan, Monitor und mehr.",
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new TextBlock
        {
            Text = $"Open Source unter der MIT-Lizenz. Copyright © 2026 {Developer}.",
            TextWrapping = TextWrapping.Wrap,
        });

        body.Children.Add(new TextBlock { Text = "Verwendete Komponenten", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        var components = new Grid { ColumnSpacing = 12, RowSpacing = 2 };
        components.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        components.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var i = 0; i < Components.Length; i++)
        {
            components.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new TextBlock { Text = Components[i].Name, TextWrapping = TextWrapping.Wrap };
            var license = new TextBlock { Text = Components[i].License, Foreground = secondary };
            Grid.SetRow(name, i);
            Grid.SetRow(license, i);
            Grid.SetColumn(license, 1);
            components.Children.Add(name);
            components.Children.Add(license);
        }

        body.Children.Add(components);
        body.Children.Add(new TextBlock
        {
            Text = "S.M.A.R.T.-Attributnamen nach ATA/ACS und öffentlicher Herstellerdokumentation; NVMe-Felder nach der NVMe Base Specification.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = secondary,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        });
        body.Children.Add(new TextBlock
        {
            Text = $"{RuntimeInformation.FrameworkDescription} · {RuntimeInformation.OSDescription} · {RuntimeInformation.ProcessArchitecture} · " +
                (Privileges.IsElevated ? "mit Administratorrechten" : "ohne Administratorrechte"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = secondary,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        });

        Content = new ScrollViewer { Content = body, MaxHeight = 560 };
    }

    /// <summary>Product version without the build metadata suffix ("+commit").</summary>
    public static string Version
    {
        get
        {
            var informational = typeof(AboutDialog).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus < 0 ? informational : informational[..plus];
        }
    }
}
