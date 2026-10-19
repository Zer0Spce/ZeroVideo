using System.ComponentModel;
using System.Windows;
using ZeroVideo.App.ViewModels;

namespace ZeroVideo.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Loaded += async (_, _) => await _vm.InitializeAsync();
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        await _vm.DisposeAsync();
        base.OnClosing(e);
    }
}
