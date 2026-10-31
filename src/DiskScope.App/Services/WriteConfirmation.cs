using System.Globalization;
using DiskScope.Core;
using DiskScope.Core.Destructive;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DiskScope.App.Services;

/// <summary>Dialogs for destructive operations (SPEC 9.4, 9.5).</summary>
public interface IWriteConfirmation
{
    /// <summary>Shows the plan; returns the typed model name, or null if cancelled or refused.</summary>
    Task<string?> ConfirmAsync(DestructivePlan plan, string operation);

    /// <summary>Dry run: shows what would be affected without writing.</summary>
    Task ShowPlanAsync(DestructivePlan plan, string operation);

    Task<bool> ConfirmSettingAsync(string title, string message);
}

public sealed class WriteConfirmation : IWriteConfirmation
{
    private static XamlRoot Root => App.MainWindow.Content.XamlRoot;

    public async Task<string?> ConfirmAsync(DestructivePlan plan, string operation)
    {
        if (!plan.Allowed)
        {
            await ShowPlanAsync(plan, operation).ConfigureAwait(true);
            return null;
        }

        var input = new TextBox { Header = $"Zum Bestätigen die Modellbezeichnung eintippen: {plan.Model}", PlaceholderText = plan.Model };
        var content = PlanPanel(plan, operation);
        content.Children.Add(input);

        var dialog = new ContentDialog
        {
            XamlRoot = Root,
            Title = $"{operation}: Daten werden gelöscht",
            PrimaryButtonText = "Daten überschreiben",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
            Content = content,
        };
        input.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = string.Equals(input.Text.Trim(), plan.Model.Trim(), StringComparison.Ordinal);

        return await dialog.ShowAsync() == ContentDialogResult.Primary ? input.Text.Trim() : null;
    }

    public async Task ShowPlanAsync(DestructivePlan plan, string operation)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root,
            Title = plan.Allowed ? $"Probelauf: {operation}" : $"{operation} ist gesperrt",
            CloseButtonText = "Schließen",
            Content = PlanPanel(plan, operation),
        };
        await dialog.ShowAsync();
    }

    public async Task<bool> ConfirmSettingAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Übernehmen",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static StackPanel PlanPanel(DestructivePlan plan, string operation)
    {
        var c = CultureInfo.CurrentCulture;
        var panel = new StackPanel { Spacing = 8, Width = 520 };
        void Line(string text, Brush? brush = null) =>
            panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = brush ?? (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"] });

        Line($"{operation} auf Laufwerk {plan.Drive.Number}: {plan.Model} (Seriennummer {plan.Serial ?? "–"})");
        Line($"Betroffener Bereich: {ByteSize.Format(plan.Range.Start, c)} – {ByteSize.Format(plan.Range.End, c)} ({plan.Bytes.ToString("N0", c)} Byte)");
        foreach (var volume in plan.Volumes)
        {
            Line($"Volume {(volume.MountPoint.Length > 0 ? volume.MountPoint : "(ohne Buchstabe)")} {volume.Label} {volume.FileSystem}, {ByteSize.Format(volume.Capacity, c)}{(plan.DismountVolumes ? " – wird gesperrt und ausgehängt" : string.Empty)}");
        }

        var error = (Brush)Application.Current.Resources["StatusErrorBrush"];
        foreach (var refusal in plan.Refusals)
        {
            Line("Gesperrt: " + refusal, error);
        }

        if (plan.Allowed)
        {
            Line("Alle Daten im Bereich gehen unwiderruflich verloren.", error);
        }

        return panel;
    }
}
