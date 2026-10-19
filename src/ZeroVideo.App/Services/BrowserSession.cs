using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Playwright;
using ZeroVideo.App.Models;

namespace ZeroVideo.App.Services;

public sealed class BrowserSession : IAsyncDisposable
{
    private const string PortStateFileName = "ZeroVideoDevToolsPort.txt";

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private Process? _edgeProcess;

    public IBrowserContext Context => _context ?? throw new InvalidOperationException("Browser not started.");

    /// <summary>
    /// Opens the real installed Microsoft Edge without Playwright or remote debugging.
    /// This is the only mode intended for account sign-in, OAuth, CAPTCHA, or verification.
    /// The user signs in normally, then closes the browser before starting automation.
    /// </summary>
    public Task OpenManualAsync(ZeroVideoSettings settings, string url)
    {
        if (_context is not null)
        {
            throw new InvalidOperationException(
                "Automation Edge is already connected. Close the current ZeroVideo Edge window and restart ZeroVideo before doing a manual sign-in.");
        }

        Directory.CreateDirectory(settings.EdgeUserDataDirectory);
        var edgePath = FindEdgeExecutable();
        var startInfo = new ProcessStartInfo(edgePath)
        {
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add($"--user-data-dir={settings.EdgeUserDataDirectory}");
        startInfo.ArgumentList.Add("--start-maximized");
        startInfo.ArgumentList.Add("--no-first-run");
        startInfo.ArgumentList.Add(url);

        Process.Start(startInfo)
            ?? throw new InvalidOperationException("Microsoft Edge could not be started.");

        return Task.CompletedTask;
    }

    public async Task EnsureStartedAsync(ZeroVideoSettings settings)
    {
        if (_context is not null) return;

        Directory.CreateDirectory(settings.EdgeUserDataDirectory);
        var endpoint = await EnsureAutomationEdgeStartedAsync(settings.EdgeUserDataDirectory);

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

    private async Task<string> EnsureAutomationEdgeStartedAsync(string userDataDirectory)
    {
        var portStateFile = Path.Combine(userDataDirectory, PortStateFileName);

        // Reattach to an already-running ZeroVideo automation session when possible.
        var existingEndpoint = TryReadSavedEndpoint(portStateFile);
        if (existingEndpoint is not null && await IsEndpointAliveAsync(existingEndpoint))
            return existingEndpoint;

        TryDelete(portStateFile);

        var port = FindAvailableLoopbackPort();
        var endpoint = $"http://127.0.0.1:{port}";
        var edgePath = FindEdgeExecutable();
        var startInfo = new ProcessStartInfo(edgePath)
        {
            UseShellExecute = false
        };

        // Account sign-in is deliberately NOT performed in this mode. Open ChatGPT /
        // Open Clipchamp first, sign in in the normal Edge window, then close it.
        // Automation reuses that dedicated profile afterward.
        startInfo.ArgumentList.Add($"--remote-debugging-port={port}");
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
                    "ZeroVideo could not start its automation browser. A manual ZeroVideo Edge window may still be open. Finish signing in, close that Edge window completely, then try again.");
            }

            if (await IsEndpointAliveAsync(endpoint))
            {
                await File.WriteAllTextAsync(portStateFile, port.ToString());
                return endpoint;
            }

            await Task.Delay(250);
        }

        TryDelete(portStateFile);
        throw new TimeoutException(
            "ZeroVideo started Microsoft Edge but could not attach to it. Close all Edge windows using the ZeroVideo profile, reopen ZeroVideo, and try again.");
    }

    private static string? TryReadSavedEndpoint(string portStateFile)
    {
        try
        {
            if (!File.Exists(portStateFile)) return null;
            var text = File.ReadAllText(portStateFile).Trim();
            if (!int.TryParse(text, out var port) || port <= 0) return null;
            return $"http://127.0.0.1:{port}";
        }
        catch
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // A stale state file is non-fatal; the connection checks below are authoritative.
        }
    }

    private static int FindAvailableLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
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
        // Disconnect the automation client without forcing the visible Edge process to
        // close. The user can close it normally and the dedicated profile remains signed in.
        _context = null;
        _browser = null;
        _playwright?.Dispose();
        _playwright = null;
        _edgeProcess = null;
        return ValueTask.CompletedTask;
    }
}
