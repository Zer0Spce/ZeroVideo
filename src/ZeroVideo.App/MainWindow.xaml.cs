using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ZeroVideo.App.ViewModels;

namespace ZeroVideo.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        SourceInitialized += (_, _) => EnableDarkTitleBar();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        AnimateShellIn();
        await _vm.InitializeAsync();
    }

    private void YouTubeTokenChanged(object sender, RoutedEventArgs e)
        => _vm.YouTubeAccessToken = YouTubeTokenBox.Password;

    private void TikTokTokenChanged(object sender, RoutedEventArgs e)
        => _vm.TikTokAccessToken = TikTokTokenBox.Password;

    private void AnimateShellIn()
    {
        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        RootShell.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(330),
            EasingFunction = easing
        });

        if (RootShell.RenderTransform is TranslateTransform translate)
        {
            translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
            {
                From = 14,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(360),
                EasingFunction = easing
            });
        }
    }

    private void EnableDarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var enabled = 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref enabled, sizeof(int)) != 0)
                _ = DwmSetWindowAttribute(hwnd, 19, ref enabled, sizeof(int));
        }
        catch
        {
            // Older Windows builds can ignore the dark title-bar hint safely.
        }
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        await _vm.DisposeAsync();
        base.OnClosing(e);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);
}
