using Microsoft.Playwright;

namespace ZeroVideo.App.Services;

public static class BrowserLocator
{
    public static async Task<ILocator?> FirstVisibleAsync(IPage page, IEnumerable<string> selectors, int timeoutMs = 1500)
    {
        foreach (var selector in selectors)
        {
            try
            {
                var locator = page.Locator(selector).First;
                await locator.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = timeoutMs });
                return locator;
            }
            catch { }
        }
        return null;
    }

    public static async Task ClickFirstAsync(IPage page, IEnumerable<string> selectors, string description, int timeoutMs = 2500)
    {
        var locator = await FirstVisibleAsync(page, selectors, timeoutMs) ?? throw new InvalidOperationException($"Could not find {description}. The site UI may have changed; update the automation profile.");
        await locator.ClickAsync();
    }
}
