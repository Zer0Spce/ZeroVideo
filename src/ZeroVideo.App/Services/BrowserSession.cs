using Microsoft.Playwright;
using ZeroVideo.App.Models;

namespace ZeroVideo.App.Services;

public sealed class BrowserSession : IAsyncDisposable
{
    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    public IBrowserContext Context => _context ?? throw new InvalidOperationException("Browser not started.");

    public async Task EnsureStartedAsync(ZeroVideoSettings settings)
    {
        if (_context is not null) return;
        Directory.CreateDirectory(settings.EdgeUserDataDirectory);
        _playwright = await Playwright.CreateAsync();
        _context = await _playwright.Chromium.LaunchPersistentContextAsync(settings.EdgeUserDataDirectory, new()
        {
            Channel = "msedge",
            Headless = false,
            ViewportSize = null,
            AcceptDownloads = true,
            Args = ["--start-maximized"]
        });
    }

    public async Task<IPage> GetOrCreatePageAsync(string hostContains)
    {
        var existing = Context.Pages.FirstOrDefault(p => p.Url.Contains(hostContains, StringComparison.OrdinalIgnoreCase));
        return existing ?? await Context.NewPageAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_context is not null) await _context.CloseAsync();
        _playwright?.Dispose();
    }
}
