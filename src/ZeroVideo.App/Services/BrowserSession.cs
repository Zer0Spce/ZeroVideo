using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Playwright;
using ZeroVideo.App.Models;

namespace ZeroVideo.App.Services;

public sealed class BrowserSession : IAsyncDisposable
{
    private const string PortStateFileName = "ZeroVideoDevToolsPort.txt";
    private const string ManualPidStateFileName = "ZeroVideoManualEdgePid.txt";

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private Process? _automationEdgeProcess;
    private Process? _manualEdgeProcess;

    public IBrowserContext Context => _context ?? throw new InvalidOperationException("Browser not started.");

    /// <summary>
    /// Opens the real installed Microsoft Edge without Playwright or remote debugging.
    /// Login, OAuth, CAPTCHA and verification happen only in this normal user-controlled window.
    /// </summary>
    public async Task OpenManualAsync(ZeroVideoSettings settings, string url)
    {
        await StopAutomationAsync();
        Directory.CreateDirectory(settings.EdgeUserDataDirectory);

        var edgePath = FindEdgeExecutable();
        var startInfo = new ProcessStartInfo(edgePath)
        {
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add($"--user-data-dir={settings.EdgeUserDataDirectory}");
        startInfo.ArgumentList.Add("--start-maximized");
        startInfo.ArgumentList.Add("--no-first-run");
        startInfo.ArgumentList.Add("--no-default-browser-check");
        startInfo.ArgumentList.Add("--disable-background-mode");
        startInfo.ArgumentList.Add(url);

        var launched = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Microsoft Edge could not be started.");

        // Keep the first long-lived process as the owner of this dedicated manual session.
        if (_manualEdgeProcess is null || HasExited(_manualEdgeProcess))
            _manualEdgeProcess = launched;
        else
            launched.Dispose();

        try
        {
            var pid = _manualEdgeProcess is not null && !HasExited(_manualEdgeProcess)
                ? _manualEdgeProcess.Id
                : launched.Id;
            await File.WriteAllTextAsync(Path.Combine(settings.EdgeUserDataDirectory, ManualPidStateFileName), pid.ToString());
        }
        catch
        {
            // The in-memory process reference is enough for the current session.
        }
    }

    public async Task EnsureStartedAsync(ZeroVideoSettings settings)
    {
        if (_context is not null) return;

        Directory.CreateDirectory(settings.EdgeUserDataDirectory);

        // If the user left the normal sign-in window open, perform the handoff automatically.
        // Only the dedicated ZeroVideo Edge process we started is closed; everyday Edge is untouched.
        await CloseManualEdgeForHandoffAsync(settings.EdgeUserDataDirectory);

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

    private async Task CloseManualEdgeForHandoffAsync(string userDataDirectory)
    {
        var pidState = Path.Combine(userDataDirectory, ManualPidStateFileName);
        Process? process = null;

        if (_manualEdgeProcess is not null && !HasExited(_manualEdgeProcess))
        {
            process = _manualEdgeProcess;
        }
        else
        {
            try
            {
                if (File.Exists(pidState) && int.TryParse(File.ReadAllText(pidState).Trim(), out var pid))
                    process = Process.GetProcessById(pid);
            }
            catch
            {
                process = null;
            }
        }

        if (process is not null && !HasExited(process))
        {
            try { process.CloseMainWindow(); } catch { }

            for (var i = 0; i < 24 && !HasExited(process); i++)
                await Task.Delay(125);

            if (!HasExited(process))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch { }
            }
        }

        try { process?.Dispose(); } catch { }
        _manualEdgeProcess = null;
        TryDelete(pidState);

        // Give Chromium enough time to flush cookies/session state and release the profile lock.
        await Task.Delay(900);
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

        startInfo.ArgumentList.Add($"--remote-debugging-port={port}");
        startInfo.ArgumentList.Add($"--user-data-dir={userDataDirectory}");
        startInfo.ArgumentList.Add("--start-maximized");
        startInfo.ArgumentList.Add("--no-first-run");
        startInfo.ArgumentList.Add("--no-default-browser-check");
        startInfo.ArgumentList.Add("--disable-background-mode");
        startInfo.ArgumentList.Add("https://chatgpt.com/");

        _automationEdgeProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Microsoft Edge could not be started.");

        for (var i = 0; i < 120; i++)
        {
            if (HasExited(_automationEdgeProcess))
            {
                throw new InvalidOperationException(
                    "ZeroVideo could not start its automation browser. Reopen ChatGPT from the Automation tab, confirm you are signed in, then try again.");
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
            "ZeroVideo started Microsoft Edge but could not attach to it. Reopen ChatGPT from the Automation tab and try again.");
    }

    private async Task StopAutomationAsync()
    {
        _context = null;
        _browser = null;
        _playwright?.Dispose();
        _playwright = null;

        if (_automationEdgeProcess is not null && !HasExited(_automationEdgeProcess))
        {
            try
            {
                _automationEdgeProcess.Kill(entireProcessTree: true);
                await _automationEdgeProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch { }
        }

        try { _automationEdgeProcess?.Dispose(); } catch { }
        _automationEdgeProcess = null;
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

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch { return true; }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
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

    public async ValueTask DisposeAsync()
    {
        await StopAutomationAsync();
        try { _manualEdgeProcess?.Dispose(); } catch { }
        _manualEdgeProcess = null;
    }
}
