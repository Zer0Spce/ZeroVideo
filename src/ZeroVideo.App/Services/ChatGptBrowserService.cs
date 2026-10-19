using System.Text.Json;
using Microsoft.Playwright;
using ZeroVideo.App.Models;

namespace ZeroVideo.App.Services;

public sealed class ChatGptBrowserService(BrowserSession browser, AutomationProfileService profiles)
{
    public async Task<VideoProject> GeneratePlanAsync(VideoProject seed, ZeroVideoSettings settings, string prompt, Action<string>? log = null, CancellationToken ct = default)
    {
        await browser.EnsureStartedAsync(settings);
        var profile = await profiles.LoadAsync(settings.AutomationProfilePath);
        var page = await browser.GetOrCreatePageAsync("chatgpt.com");
        await page.GotoAsync(settings.ChatGptUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 120000 });
        log?.Invoke("ChatGPT opened. If login or verification is shown, complete it in the browser; ZeroVideo will never try to bypass it.");

        var input = await WaitForPromptAsync(page, profile.ChatGpt.PromptInputs, ct);
        await input.ClickAsync();
        await input.FillAsync(prompt);
        var assistantSelector = string.Join(",", profile.ChatGpt.AssistantMessages);
        var before = await page.Locator(assistantSelector).CountAsync();
        var send = await BrowserLocator.FirstVisibleAsync(page, profile.ChatGpt.SendButtons, 1500);
        if (send is not null) await send.ClickAsync(); else await input.PressAsync("Enter");
        log?.Invoke("Waiting for ChatGPT plan...");

        var text = await WaitForStableAssistantAsync(page, assistantSelector, before, ct);
        var parsed = ParseProject(text, seed);
        log?.Invoke($"Received {parsed.Chapters.Count} chapters / {parsed.Chapters.Sum(c => c.Scenes.Count)} scenes.");
        return parsed;
    }

    public async Task OpenAsync(ZeroVideoSettings settings)
    {
        await browser.EnsureStartedAsync(settings);
        var page = await browser.GetOrCreatePageAsync("chatgpt.com");
        await page.GotoAsync(settings.ChatGptUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 120000 });
    }

    private static async Task<ILocator> WaitForPromptAsync(IPage page, IEnumerable<string> selectors, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var locator = await BrowserLocator.FirstVisibleAsync(page, selectors, 1200);
            if (locator is not null) return locator;
            await Task.Delay(1000, ct);
        }
        throw new OperationCanceledException(ct);
    }

    private static async Task<string> WaitForStableAssistantAsync(IPage page, string selector, int previousCount, CancellationToken ct)
    {
        string last = "";
        var stable = 0;
        var started = DateTime.UtcNow;
        while (!ct.IsCancellationRequested && DateTime.UtcNow - started < TimeSpan.FromMinutes(12))
        {
            var messages = page.Locator(selector);
            var count = await messages.CountAsync();
            if (count > previousCount || count > 0)
            {
                var current = (await messages.Last.InnerTextAsync()).Trim();
                if (current.Length > 20 && current == last) stable++; else stable = 0;
                last = current;
                if (stable >= 3) return current;
            }
            await Task.Delay(2000, ct);
        }
        if (!string.IsNullOrWhiteSpace(last)) return last;
        throw new TimeoutException("ChatGPT response was not detected. Check the browser and automation selectors.");
    }

    private static VideoProject ParseProject(string raw, VideoProject seed)
    {
        var json = ExtractJson(raw);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        seed.Title = root.TryGetProperty("title", out var title) ? title.GetString() ?? seed.Topic : seed.Topic;
        seed.Sources = root.TryGetProperty("sources", out var sources) ? sources.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : [];
        seed.Chapters = JsonSerializer.Deserialize<List<ProjectChapter>>(root.GetProperty("chapters").GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        return seed;
    }

    private static string ExtractJson(string raw)
    {
        raw = raw.Trim();
        if (raw.StartsWith("```"))
        {
            var firstNewLine = raw.IndexOf('\n');
            var lastFence = raw.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewLine >= 0 && lastFence > firstNewLine) raw = raw[(firstNewLine + 1)..lastFence].Trim();
        }
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidDataException("ChatGPT did not return a JSON object.");
        return raw[start..(end + 1)];
    }
}
