using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace ZeroVideo.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // WPF can fail before MainWindow.Loaded (including while loading XAML).
        // Report that failure instead of silently disappearing with no error.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                StartupDiagnostics.Report(ex, "AppDomain unhandled exception");
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            StartupDiagnostics.Report(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };
        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        StartupDiagnostics.Report(e.Exception, "WPF dispatcher");
        e.Handled = true;
        if (string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("ZEROVIDEO_STARTUP_SMOKE_MARKER")))
        {
            try
            {
                MessageBox.Show(
                    "ZeroVideo encountered an error and could not continue.\n\n" +
                    e.Exception.Message + "\n\nCrash details: " + StartupDiagnostics.LogPath,
                    "ZeroVideo startup error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { /* Logging must still work if the dialog cannot be displayed. */ }
        }
        Shutdown(1);
    }
}

internal static class StartupDiagnostics
{
    internal static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ZeroVideo", "diagnostics", "startup-crash.log");

    internal static void Report(Exception exception, string stage)
    {
        try
        {
            var path = LogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path,
                $"[{DateTimeOffset.Now:O}] {stage}\n{exception}\n\n");
        }
        catch
        {
            // Never mask the original startup error with a logging failure.
        }
    }
}
