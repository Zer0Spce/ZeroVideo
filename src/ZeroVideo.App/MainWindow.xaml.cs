using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using ZeroVideo.App.Services;
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
    private List<TabItem>? _allTabs;
    private readonly Dictionary<string, CheckBox> _tabVisibilityChecks = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        SourceInitialized += (_, _) => EnableDarkTitleBar();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            AnimateShellIn();
            await _vm.InitializeAsync();
            SetupTabVisibility();
            PixabayKeyInput.Password = _vm.Settings.PixabayApiKey ?? "";

            // CI tests the exact published EXE, not only a successful build.
            var marker = Environment.GetEnvironmentVariable("ZEROVIDEO_STARTUP_SMOKE_MARKER");
            if (!string.IsNullOrWhiteSpace(marker))
            {
                System.IO.File.WriteAllText(marker, "ZeroVideo main window and settings loaded.");
                Application.Current.Shutdown(0);
                return;
            }

            // Also prove that the packaged .playwright/node driver actually runs.
            // This catches missing hidden files before sending a test ZIP to users.
            var playwrightMarker = Environment.GetEnvironmentVariable("ZEROVIDEO_PLAYWRIGHT_SMOKE_MARKER");
            if (!string.IsNullOrWhiteSpace(playwrightMarker))
            {
                using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
                System.IO.File.WriteAllText(playwrightMarker, "Playwright driver started from portable build.");
                Application.Current.Shutdown(0);
                return;
            }
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Report(ex, "MainWindow loaded / user settings initialization");
            if (string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable("ZEROVIDEO_STARTUP_SMOKE_MARKER")) &&
                string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable("ZEROVIDEO_PLAYWRIGHT_SMOKE_MARKER")))
            {
                MessageBox.Show(
                    "ZeroVideo could not finish starting.\n\n" +
                    ex.Message + "\n\nDetails: " + StartupDiagnostics.LogPath,
                    "ZeroVideo startup error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            Application.Current.Shutdown(1);
        }
    }

    private void SetupTabVisibility()
    {
        _allTabs ??= MainTabs.Items.OfType<TabItem>().ToList();
        var preferences = _vm.Settings.TabVisibility ??= new Dictionary<string, bool>(StringComparer.Ordinal);
        TabVisibilityOptions.Children.Clear();
        _tabVisibilityChecks.Clear();
        foreach (var tab in _allTabs)
        {
            var name = tab.Header?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(name)) continue;
            var alwaysVisible = name == "Settings";
            var check = new CheckBox
            {
                Content = name,
                IsChecked = alwaysVisible || !preferences.TryGetValue(name, out var enabled) || enabled,
                IsEnabled = !alwaysVisible,
                MinWidth = 175,
                Margin = new Thickness(0, 5, 8, 5),
                ToolTip = alwaysVisible ? "Settings cannot be hidden." : "Show or hide " + name
            };
            if (!alwaysVisible)
            {
                check.Checked += TabVisibilityChanged;
                check.Unchecked += TabVisibilityChanged;
            }
            _tabVisibilityChecks[name] = check;
            TabVisibilityOptions.Children.Add(check);
        }
        ApplyTabVisibility();
    }

    private void ApplyTabVisibility()
    {
        if (_allTabs is null) return;
        foreach (var tab in _allTabs)
            if (MainTabs.Items.Contains(tab))
                MainTabs.Items.Remove(tab);
        foreach (var tab in _allTabs)
        {
            var name = tab.Header?.ToString() ?? "";
            if (name == "Settings" || !_tabVisibilityChecks.TryGetValue(name, out var check) || check.IsChecked == true)
                MainTabs.Items.Add(tab);
        }
        // Keep an accessible, visible tab selected after hiding the current one.
        if (MainTabs.SelectedItem is null && MainTabs.Items.Count > 0)
            MainTabs.SelectedIndex = 0;
    }

    private async void TabVisibilityChanged(object sender, RoutedEventArgs e)
    {
        if (_allTabs is null) return;
        foreach (var (name, check) in _tabVisibilityChecks)
            if (name != "Settings") _vm.Settings.TabVisibility[name] = check.IsChecked == true;
        ApplyTabVisibility();
        try { await new SettingsService().SaveAsync(_vm.Settings); }
        catch (Exception ex) { MessageBox.Show("Could not save tab visibility: " + ex.Message); }
    }

    private void ShowAllTabs_OnClick(object sender, RoutedEventArgs e)
    {
        foreach (var check in _tabVisibilityChecks.Values)
            if (check.IsEnabled) check.IsChecked = true;
    }

    private void OnPixabayKeyChanged(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.PasswordBox box)
            _vm.Settings.PixabayApiKey = box.Password;
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

    // Dragging a calendar entry only updates an unsubmitted local queue
    // date. Actual TikTok Studio posts cannot be moved from this calendar.
    private void OnCalendarEntryPreviewMouseMove(object sender,
        System.Windows.Input.MouseEventArgs e)
    {
        if(e.LeftButton!=System.Windows.Input.MouseButtonState.Pressed)return;
        if(e.OriginalSource is not FrameworkElement element ||
           element.DataContext is not CalendarVideoEntry entry)return;
        DragDrop.DoDragDrop((DependencyObject)sender,
            new DataObject(typeof(CalendarVideoEntry),entry),DragDropEffects.Move);
    }

    private void OnCalendarDayDrop(object sender,DragEventArgs e)
    {
        if(sender is not System.Windows.Controls.Border border ||
           border.DataContext is not CalendarDayEntry day ||
           e.Data.GetData(typeof(CalendarVideoEntry)) is not CalendarVideoEntry entry)
            return;
        _vm.MoveCalendarVideoToDay(entry,day.Date);
        e.Handled=true;
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
        System.Diagnostics.Debug.WriteLine("ZeroVideo local preview codec error: " +
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
