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
        await SendAsync(page, input, profile.ChatGpt.SendButtons);
        log?.Invoke("Waiting for ChatGPT plan...");

        var text = await WaitForStableAssistantAsync(page, assistantSelector, before, ct);
        var parsed = ParseProject(text, seed);
        log?.Invoke($"Received {parsed.Chapters.Count} chapters / {parsed.Chapters.Sum(c => c.Scenes.Count)} scenes.");
        return parsed;
    }

    public async Task<string> GenerateSceneImageAsync(VideoProject project, ProjectChapter chapter, ProjectScene scene, ZeroVideoSettings settings, Action<string>? log = null, CancellationToken ct = default)
    {
        await browser.EnsureStartedAsync(settings);
        var profile = await profiles.LoadAsync(settings.AutomationProfilePath);
        if (profile.ChatGpt.GeneratedImages.Count == 0) throw new InvalidOperationException("No ChatGPT generated-image selectors are configured.");

        var page = await browser.GetOrCreatePageAsync("chatgpt.com");
        await page.GotoAsync(settings.ChatGptUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 120000 });
        var input = await WaitForPromptAsync(page, profile.ChatGpt.PromptInputs, ct);
        var imageSelector = string.Join(",", profile.ChatGpt.GeneratedImages);
        var before = await page.Locator(imageSelector).CountAsync();
        var ratioWords = project.AspectRatio == "9:16" ? "vertical 9:16" : "widescreen 16:9";
        var prompt = $"""
Create ONE original {ratioWords} documentary-style still image for this video scene.
TOPIC: {project.Topic}
SCENE NARRATION: {scene.Narration}
VISUAL DIRECTION: {scene.ImagePrompt}
SEARCH/REFERENCE IDEA: {scene.VisualQuery}
VIDEO STYLE: {project.Style}

Requirements: one single image only; no collage; no captions; no readable text; no logos; no watermark; no UI; visually clear at YouTube resolution. Keep the subject and era accurate to the scene. Do not add unrelated people or objects.
""";
        await input.ClickAsync();
        await input.FillAsync(prompt);
        await SendAsync(page, input, profile.ChatGpt.SendButtons);
        log?.Invoke($"Scene {chapter.Number}.{scene.Number}: waiting for ChatGPT visual...");

        var image = await WaitForNewImageAsync(page, imageSelector, before, ct);
        var destination = ProjectMedia.GeneratedImagePath(project, chapter, scene);
        await image.ScreenshotAsync(new() { Path = destination, Type = ScreenshotType.Png });
        scene.MediaPath = destination;
        log?.Invoke($"Scene {chapter.Number}.{scene.Number}: visual saved.");
        return destination;
    }

    public async Task OpenAsync(ZeroVideoSettings settings)
    {
        await browser.EnsureStartedAsync(settings);
        var page = await browser.GetOrCreatePageAsync("chatgpt.com");
        await page.GotoAsync(settings.ChatGptUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 120000 });
    }

    private static async Task SendAsync(IPage page, ILocator input, IEnumerable<string> sendButtons)
    {
        var send = await BrowserLocator.FirstVisibleAsync(page, sendButtons, 1500);
        if (send is not null) await send.ClickAsync(); else await input.PressAsync("Enter");
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

    private static async Task<ILocator> WaitForNewImageAsync(IPage page, string selector, int previousCount, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        while (!ct.IsCancellationRequested && DateTime.UtcNow - started < TimeSpan.FromMinutes(8))
        {
            var images = page.Locator(selector);
            var count = await images.CountAsync();
            if (count > previousCount)
            {
                var latest = images.Last;
                try
                {
                    await latest.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5000 });
                    var box = await latest.BoundingBoxAsync();
                    if (box is not null && box.Width >= 200 && box.Height >= 200) return latest;
                }
                catch { }
            }
            await Task.Delay(2500, ct);
        }
        throw new TimeoutException("ChatGPT image generation did not produce a detectable image. Check the visible browser and image selectors.");
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
