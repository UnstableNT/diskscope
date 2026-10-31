using DiskScope.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace DiskScope.App.Views;

public sealed partial class FolderUsageView : UserControl, IViewWithResults
{
    public FolderUsageView()
    {
        ViewModel = App.Services.GetRequiredService<FolderUsageViewModel>();
        InitializeComponent();
    }

    public FolderUsageViewModel ViewModel { get; }

    public IResultText Results => ViewModel;

    private void OnExpanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Item is FolderItem item)
        {
            item.Realize(ViewModel.Sort);
        }
    }

    private async void OnPickFolder(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        if (await picker.PickSingleFolderAsync() is { } folder)
        {
            ViewModel.SetCustomTarget(folder.Path);
        }
    }
}
