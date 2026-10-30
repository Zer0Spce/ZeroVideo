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

    // Only controls the embedded local video preview. The TikTok uploader
    // and video rendering/playback engine are untouched.
    private void OnLibraryPlayClicked(object sender, RoutedEventArgs e)
    {
        try { LibraryPreviewPlayer.Play(); }
        catch (Exception ex) { MessageBox.Show("Video preview: " + ex.Message); }
    }

    private void OnLibraryPauseClicked(object sender, RoutedEventArgs e)
    {
        try { LibraryPreviewPlayer.Pause(); }
        catch (Exception ex) { MessageBox.Show("Video preview: " + ex.Message); }
    }

    private void OnLibraryStopClicked(object sender, RoutedEventArgs e)
    {
        try { LibraryPreviewPlayer.Stop(); }
        catch (Exception ex) { MessageBox.Show("Video preview: " + ex.Message); }
    }

    private void OnLibraryMediaFailed(object sender,
        System.Windows.ExceptionRoutedEventArgs e)
    {
        // The underlying Windows MediaElement may not support certain codecs.
        // The Open Full Video action always remains available.
        _vm.Log("Local library preview codec error: " +
            (e.ErrorException?.Message ?? "Unknown Windows MediaElement error"));
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        await _vm.DisposeAsync();
        base.OnClosing(e);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);
}
