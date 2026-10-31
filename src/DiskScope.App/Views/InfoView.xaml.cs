using DiskScope.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace DiskScope.App.Views;

public sealed partial class InfoView : UserControl, IViewWithResults
{
    public InfoView()
    {
        ViewModel = App.Services.GetRequiredService<InfoViewModel>();
        InitializeComponent();
    }

    public InfoViewModel ViewModel { get; }

    public IResultText Results => ViewModel;
}
