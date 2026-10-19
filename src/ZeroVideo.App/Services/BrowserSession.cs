using System.Diagnostics;
using Microsoft.Playwright;
using ZeroVideo.App.Models;

namespace ZeroVideo.App.Services;

public sealed class BrowserSession : IAsyncDisposable
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private Process? _edgeProcess;

    public IBrowserContext Context => _context ?? throw new InvalidOperationException("Browser not started.");

    public async Task EnsureStartedAsync(ZeroVideoSettings settings)
    {
        if (_context is not null) return;

        Directory.CreateDirectory(settings.EdgeUserDataDirectory);

        // Important: do NOT launch Edge through Playwright. Google can reject OAuth
        // in browsers launched with automation flags. Start the user's real installed
        // Microsoft Edge normally, then attach Playwright over the local DevTools port.
        var endpoint = await EnsureNormalEdgeStartedAsync(settings.EdgeUserDataDirectory);

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.ConnectOverCDPAsync(endpoint, new()
        {
            Timeout = 30_000
        });

        _context = _browser.Contexts.FirstOrDefault()
            ?? throw new InvalidOperationException("Connected to Microsoft Edge, but no browser context was available.");
    }

    public async Task<IPage> GetOrCreatePageAsync(string hostContains)
    {
        var existing = Context.Pages.FirstOrDefault(p =>
            p.Url.Contains(hostContains, StringComparison.OrdinalIgnoreCase));

        return existing ?? await Context.NewPageAsync();
    }

    private async Task<string> EnsureNormalEdgeStartedAsync(string userDataDirectory)
    {
        var devToolsPortFile = Path.Combine(userDataDirectory, "DevToolsActivePort");

        // Reattach to an already-running ZeroVideo Edge session when possible.
        var existingEndpoint = TryReadEndpoint(devToolsPortFile);
        if (existingEndpoint is not null && await IsEndpointAliveAsync(existingEndpoint))
            return existingEndpoint;

        try
        {
            if (File.Exists(devToolsPortFile)) File.Delete(devToolsPortFile);
        }
        catch
        {
            // A stale marker is harmless; Edge will rewrite it when it starts.
        }

        var edgePath = FindEdgeExecutable();
        var startInfo = new ProcessStartInfo(edgePath)
        {
            UseShellExecute = false
        };

        // Port 0 asks Edge to choose an available localhost port and write it to
        // DevToolsActivePort. The dedicated user-data directory is required by newer
        // Chromium builds for remote debugging and also keeps ZeroVideo separate from
        // the user's everyday Edge profile.
        startInfo.ArgumentList.Add("--remote-debugging-port=0");
        startInfo.ArgumentList.Add($"--user-data-dir={userDataDirectory}");
        startInfo.ArgumentList.Add("--start-maximized");
        startInfo.ArgumentList.Add("--no-first-run");
        startInfo.ArgumentList.Add("https://chatgpt.com/");

        _edgeProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Microsoft Edge could not be started.");

        for (var i = 0; i < 120; i++)
        {
            if (_edgeProcess.HasExited)
            {
                throw new InvalidOperationException(
                    "Microsoft Edge exited before ZeroVideo could connect. Close any ZeroVideo Edge windows and try again.");
            }

            var endpoint = TryReadEndpoint(devToolsPortFile);
            if (endpoint is not null && await IsEndpointAliveAsync(endpoint))
                return endpoint;

            await Task.Delay(250);
        }

        throw new TimeoutException(
            "ZeroVideo started Microsoft Edge but could not attach to it. Close the ZeroVideo Edge window, then try Open ChatGPT again.");
    }

    private static string? TryReadEndpoint(string devToolsPortFile)
    {
        try
        {
            if (!File.Exists(devToolsPortFile)) return null;
            var lines = File.ReadAllLines(devToolsPortFile);
            if (lines.Length == 0 || !int.TryParse(lines[0], out var port)) return null;
            return $"http://127.0.0.1:{port}";
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> IsEndpointAliveAsync(string endpoint)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            using var response = await client.GetAsync($"{endpoint}/json/version");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static string FindEdgeExecutable()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Edge", "Application", "msedge.exe")
        };

        var edgePath = candidates.FirstOrDefault(File.Exists);
        if (edgePath is null)
        {
            throw new FileNotFoundException(
                "Microsoft Edge was not found. Install Microsoft Edge or repair your Edge installation, then try again.");
        }

        return edgePath;
    }

    public ValueTask DisposeAsync()
    {
        // Dispose the Playwright connection only. Do not close the real Edge process;
        // keeping it open preserves the human login flow and lets the user finish any
        // verification step without ZeroVideo killing the browser underneath them.
        _context = null;
        _browser = null;
        _playwright?.Dispose();
        _playwright = null;
        _edgeProcess = null;
        return ValueTask.CompletedTask;
    }
}
