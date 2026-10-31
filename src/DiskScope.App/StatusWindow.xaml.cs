using DiskScope.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace DiskScope.App;

public sealed partial class StatusWindow : Window
{
    public StatusWindow()
    {
        Monitor = App.Services.GetRequiredService<DriveStatusMonitor>();
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(460, 360));
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "DiskScope.ico"));
        TitleBarTheme.Apply(this, AppTitleBar);
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsMaximizable = false;
        }
    }

    public DriveStatusMonitor Monitor { get; }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await Monitor.RefreshAsync().ConfigureAwait(true);
}
